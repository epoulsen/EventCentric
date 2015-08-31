﻿using EventCentric.Transport;
using System.Collections.Concurrent;

namespace EventCentric.Polling
{
    public class BufferedSubscription
    {
        public BufferedSubscription(string streamType, string url, int currentBufferVersion, bool isPoisoned)
        {
            this.StreamType = streamType;
            this.Url = url;
            this.CurrentBufferVersion = currentBufferVersion;
            this.IsPolling = false;
            this.IsPoisoned = isPoisoned;
            this.NewEventsQueue = new ConcurrentQueue<NewRawEvent>();
            this.EventsInProcessorBag = new ConcurrentBag<EventInProcessorBucket>();
        }

        public string StreamType { get; private set; }
        public string Url { get; private set; }

        /// <summary>
        /// The processor buffer version is the lowest event collection version that the processor was handling.
        /// </summary>
        public volatile int CurrentBufferVersion;
        public volatile bool IsPolling;
        public volatile bool IsPoisoned;
        public volatile ConcurrentQueue<NewRawEvent> NewEventsQueue;
        public volatile ConcurrentBag<EventInProcessorBucket> EventsInProcessorBag;
    }
}