﻿using EventCentric.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;

namespace EventCentric.Transport
{
    public class MultiNodeClient<TEnum> : IMultiNodeClient<TEnum> where TEnum : struct, IConvertible
    {
        private readonly string sharedToken;
        private readonly Dictionary<TEnum, string> nodes;

        public MultiNodeClient(string sharedToken, params KeyValuePair<TEnum, string>[] nodes)
        {
            if (!typeof(TEnum).IsEnum)
                throw new InvalidOperationException("Type TEnum must be an enumeration");

            Ensure.Positive(nodes.Count(), $"{nameof(nodes)} count");
            Ensure.NotNull(sharedToken, nameof(sharedToken));

            this.nodes = new Dictionary<TEnum, string>(nodes.Count());
            this.nodes.AddRange(nodes);
            this.sharedToken = sharedToken;
        }

        public TResponse Send<TRequest, TResponse>(string url, TRequest payload)
        {
            HttpResponseMessage response;
            using (var client = this.HttpClientFactory())
            {
                response = client.PostAsJsonAsync(url, payload).Result;
            }
            return response.IsSuccessStatusCode ? response.Content.ReadAsAsync<TResponse>().Result : default(TResponse);
        }

        public TResponse Send<TRequest, TResponse>(TEnum node, string url, TRequest payload) => this.Send<TRequest, TResponse>(this.nodes[node] + url, payload);

        public IDictionary<TEnum, string> Nodes { get; }

        private HttpClient HttpClientFactory()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.sharedToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }
    }
}