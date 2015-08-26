﻿using EventCentric.Serialization;
using EventCentric.Transport;
using System;
using System.Collections.Generic;
using System.Threading;

namespace EventCentric.Tests.Pulling.Helpers
{
    public class TestHttpClientWithSingleResult : IOldHttpPoller
    {
        private readonly JsonTextSerializer serializer = new JsonTextSerializer();
        private Guid streamId;

        private bool NewEventToBeFound;

        public TestHttpClientWithSingleResult(Guid streamId, bool newEventToBeFound = true)
        {
            this.NewEventToBeFound = newEventToBeFound;
            this.streamId = streamId;
        }

        public PollEventsResponse PollEvents(string url)
        {
            Thread.Sleep(1000);

            var list = new List<PolledEventData>();

            if (this.NewEventToBeFound)
            {
                var payload = this.serializer.Serialize(new TestEvent1());
                list.Add(new PolledEventData("Clients", this.streamId, true, payload));
            }
            else
                list.Add(new PolledEventData("Clients", this.streamId, false, string.Empty));


            return new PollEventsResponse(true, list);
        }

        public PollStreamsResponse PollStreams(string url)
        {
            throw new NotImplementedException();
        }
    }
}
