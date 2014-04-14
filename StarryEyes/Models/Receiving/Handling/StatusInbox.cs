﻿using System;
using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StarryEyes.Annotations;
using StarryEyes.Anomaly.TwitterApi.DataModels;
using StarryEyes.Models.Databases;
using StarryEyes.Models.Timelines.Statuses;

namespace StarryEyes.Models.Receiving.Handling
{
    /// <summary>
    /// Accept received statuses from any sources
    /// </summary>
    public static class StatusInbox
    {
        private static readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);

        private static readonly ConcurrentQueue<StatusNotification> _queue =
            new ConcurrentQueue<StatusNotification>();

        private static readonly ConcurrentDictionary<long, DateTime> _removes =
            new ConcurrentDictionary<long, DateTime>();

        private static readonly TimeSpan _threshold = TimeSpan.FromMinutes(5);

        private static Thread _pumpThread;

        private static volatile bool _isHaltRequested;

        private static long _lastReceivedTimestamp;

        private static DateTime _cleanupPeriod;

        static StatusInbox()
        {
            App.ApplicationFinalize += () =>
            {
                _isHaltRequested = true;
                _signal.Set();
            };
        }

        internal static void Initialize()
        {
            _cleanupPeriod = DateTime.Now;
            _pumpThread = new Thread(StatusPump);
            _pumpThread.Start();
        }

        public static void Enqueue([NotNull] TwitterStatus status)
        {
            if (status == null) throw new ArgumentNullException("status");
            // store original status first
            if (status.RetweetedOriginal != null)
            {
                Enqueue(status.RetweetedOriginal);
            }
            _queue.Enqueue(new StatusNotification(status));
            _signal.Set();
        }

        public static void EnqueueRemoval(long id)
        {
            _queue.Enqueue(new StatusNotification(id));
        }

        private static async void StatusPump()
        {
            StatusNotification n;
            while (true)
            {
                _signal.Reset();
                while (_queue.TryDequeue(out n) && !_isHaltRequested)
                {
                    if (n.IsAdded)
                    {
                        var removed = IsRegisteredAsRemoved(n.Status.Id) ||
                                      (n.Status.RetweetedOriginalId != null &&
                                       IsRegisteredAsRemoved(n.Status.RetweetedOriginalId.Value));
                        if (removed || !await StatusReceived(n.Status))
                        {
                            // already received
                            continue;
                        }
                        StatusBroadcaster.Enqueue(n);
                    }
                    else
                    {
                        StatusDeleted(n.StatusId);
                    }
                    // post next 
                    _signal.Reset();
                }
                if (_isHaltRequested)
                {
                    break;
                }
                _signal.Wait();
            }
        }

        private static async Task<bool> StatusReceived(TwitterStatus status)
        {
            try
            {
                if (!await CheckReceiveNew(status.Id))
                {
                    // already received
                    return false;
                }
                StatusProxy.StoreStatus(status);
                return true;
            }
            catch (SQLiteException)
            {
                // enqueue for retry 
                Enqueue(status);

                // and return "already received" sign
                return false;
            }
        }

        private static async Task<bool> CheckReceiveNew(long id)
        {
            // check new status based on timestamps
            var stamp = GetTimestampFromSnowflakeId(id);
            if (stamp > _lastReceivedTimestamp)
            {
                _lastReceivedTimestamp = stamp;
                return false;
            }
            // check status based on model cache
            if (StatusModel.GetIfCacheIsAlive(id) != null)
            {
                return false;
            }
            // check with database
            return !(await StatusProxy.IsStatusExistsAsync(id));
        }

        private static long GetTimestampFromSnowflakeId(long id)
        {
            // [42bit:timestamp][10bit:machine_id][12bit:sequence_id];64bit
            return id >> 22;
        }

        private static bool IsRegisteredAsRemoved(long id)
        {
            return _removes.ContainsKey(id);
        }

        private static void StatusDeleted(long statusId)
        {
            // registered as removed status
            _removes[statusId] = DateTime.Now;
            Task.Run(async () =>
            {
                // find removed statuses
                var removeds = await StatusProxy.RemoveStatusAsync(statusId);

                // notify removed ids
                foreach (var removed in removeds)
                {
                    _removes[removed] = DateTime.Now;
                    StatusBroadcaster.Enqueue(new StatusNotification(removed));
                }

                // check cleanup cycle
                var stamp = DateTime.Now;
                if (stamp - _cleanupPeriod > _threshold)
                {
                    // update period stamp
                    _cleanupPeriod = stamp;

                    // remove expireds
                    _removes.Where(t => (stamp - t.Value) > _threshold)
                            .ForEach(t =>
                            {
                                // remove expired
                                DateTime value;
                                _removes.TryRemove(t.Key, out value);
                            });
                }
            });
        }
    }
}
