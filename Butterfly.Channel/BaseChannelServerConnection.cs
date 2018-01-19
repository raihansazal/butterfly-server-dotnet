﻿/*
 * Copyright 2017 Fireshark Studios, LLC
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *     http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;

using Nito.AsyncEx;
using NLog;

using Butterfly.Util;

using Dict = System.Collections.Generic.Dictionary<string, object>;

namespace Butterfly.Channel {

    /// <inheritdoc/>
    /// <summary>
    /// Base class implementing <see cref="IChannelServerConnection"/>. New implementations will normally extend this class.
    /// </summary>
    public abstract class BaseChannelServerConnection : IChannelServerConnection {
        protected static readonly Logger logger = LogManager.GetCurrentClassLogger();

        protected readonly BaseChannelServer channelServer;
        protected readonly RegisteredRoute registeredRoute;
        protected readonly DateTime created;

        protected readonly ConcurrentQueue<string> buffer = new ConcurrentQueue<string>();
        protected readonly AsyncMonitor monitor = new AsyncMonitor();

        protected object authToken;
        protected string id = null;

        /// <summary>
        /// Stores when the datetime of the last heartbeat (set via <ref>Heartbeat</ref>)
        /// </summary>
        protected DateTime lastHeartbeat = DateTime.Now;

        public BaseChannelServerConnection(BaseChannelServer channelServer, RegisteredRoute registeredRoute) {
            this.channelServer = channelServer;
            this.registeredRoute = registeredRoute;
            this.created = DateTime.Now;
        }

        public object AuthToken => this.authToken;

        public string Id => this.id;

        public DateTime Created => this.created;

        public RegisteredRoute RegisteredRoute => this.registeredRoute;

        /// <summary>
        /// When the last heartbeat was registered
        /// </summary>
        public DateTime LastHeartbeat => this.lastHeartbeat;

        /// <summary>
        /// Implementing classes should call this periodically to keep the channel alive (otherwise <ref>ChannelServer</ref> will remove the channel)
        /// </summary>
        internal void Heartbeat() {
            logger.Trace($"Heartbeat()");
            this.lastHeartbeat = DateTime.Now;
        }

        /// <summary>
        /// Queue an object to be sent over the channel to the client.  The queue is processed by a background thread when the Channel is started.
        /// </summary>
        /// <param name="channelKey">The value to be sent to the client (will be converted to JSON)</param>
        /// <param name="value">The value to be sent to the client (will be converted to JSON)</param>
        public void Queue(object value, string channelKey = "default") {
            string json = JsonUtil.Serialize(value);
            var text = $"{channelKey}:{json}";
            this.buffer.Enqueue(text);
            this.monitor.PulseAll();
        }

        protected bool started = false;
        public void Start(object authToken, string id) {
            this.authToken = authToken;
            this.id = id;
            this.started = true;
            Task.Run(() => this.RunAsync());
        }

        protected readonly Dictionary<string, Channel> channelByKey = new Dictionary<string, Channel>();

        protected async Task RunAsync() {
            try {
                while (this.started) {
                    if (this.buffer.TryDequeue(out string result)) {
                        await this.SendAsync(result);
                    }
                    else {
                        using (await this.monitor.EnterAsync()) {
                            await this.monitor.WaitAsync();
                        }
                    }
                }
            }
            finally {
                foreach (var channel in channelByKey.Values) {
                    channel.Dispose();
                }
            }
        }

        /// <summary>
        /// Implementing classes must override this to actually send the text to the client
        /// </summary>
        protected abstract Task SendAsync(string text);

        public void ReceiveMessage(string text) {
            if (text == "!") {
                this.Heartbeat();
            }
            else {
                int pos = text.IndexOf(':');
                if (pos > 0) {
                    string name = text.Substring(0, pos).Trim();
                    string value = text.Substring(pos + 1).Trim();
                    logger.Debug($"ReceiveMessage():name={name},value={value}");
                    if (name == HttpRequestHeader.Authorization.ToString()) {
                        try {
                            var authenticationHeaderValue = AuthenticationHeaderValue.Parse(value);
                            Task task = this.channelServer.AuthenticateAsync(authenticationHeaderValue.Scheme, authenticationHeaderValue.Parameter, this);
                        }
                        catch (Exception e) {
                            logger.Error(e);
                        }
                    }
                    else if (name == "Subscriptions") {
                        try {
                            Dict[] subscriptions = JsonUtil.Deserialize<Dict[]>(value);
                            Task task = this.SubscribeAsync(subscriptions);
                        }
                        catch (Exception e) {
                            logger.Error(e);
                        }
                    }
                    else {
                        logger.Warn($"ReceiveMessage():Unknown message '{name}'");
                    }
                }
            }
        }

        protected async Task SubscribeAsync(ICollection<Dict> subscriptions) {
            try {
                logger.Debug($"SubscribeAsync()");

                var channelKeys = subscriptions.Select(x => x.GetAs("channelKey", (string)null));
                logger.Debug($"SubscribeAsync():channelKeys={string.Join(", ", channelKeys)}");

                var channelKeysToDelete = this.channelByKey.Keys.ToList();

                foreach (var subscription in subscriptions) {
                    var channelKey = subscription.GetAs("channelKey", (string)null);
                    logger.Debug($"SubscribeAsync():channelKey={channelKey}");

                    var vars = subscription.GetAs("vars", (Dict)null);
                    logger.Debug($"SubscribeAsync():vars={vars}");

                    bool addChannel = false;
                    if (!this.channelByKey.TryGetValue(channelKey, out Channel existingChannel)) {
                        addChannel = true;
                    }
                    else {
                        if (vars.Except(existingChannel.Vars).Count() > 0 || existingChannel.Vars.Except(vars).Count() > 0) {
                            existingChannel.Dispose();
                            this.channelByKey.Remove(channelKey);
                            addChannel = true;
                        }
                        channelKeysToDelete.Remove(channelKey);
                    }
                    logger.Debug($"SubscribeAsync():addChannel={addChannel},registeredRoute.RegisteredChannelByKey.Count={this.registeredRoute.RegisteredChannelByKey.Count}");

                    if (addChannel) {
                        var channel = new Channel(this, channelKey, vars);
                        if (this.registeredRoute.RegisteredChannelByKey.TryGetValue(channelKey, out RegisteredChannel registeredChannel)) {
                            var disposable = registeredChannel.handle != null ? registeredChannel.handle(vars, channel) : await registeredChannel.handleAsync(vars, channel);
                            if (disposable != null) {
                                channel.Attach(disposable);
                            }
                        }
                        else {
                            logger.Debug($"SubscribeAsync():Unknown registered channel key '{channelKey}'");
                        }
                        this.channelByKey.Add(channelKey, channel);
                    }
                }

                foreach (var channelPath in channelKeysToDelete) {
                    if (!this.channelByKey.TryGetValue(channelPath, out Channel existingChannel)) {
                        existingChannel.Dispose();
                        this.channelByKey.Remove(channelPath);
                    }
                }
            }
            catch (Exception e) {
                logger.Error(e);
            }
        }



        /// <summary>
        /// Implements the IDispose interface
        /// </summary>
        public void Dispose() {
            this.started = false;

            foreach (var channel in this.channelByKey.Values) {
                channel.Dispose();
            }
            this.channelByKey.Clear();

            this.monitor.PulseAll();

            this.DoDispose();
        }

        /// <summary>
        /// Implementing classes may optionally override this to cleanup resources as appropriate
        /// </summary>
        protected virtual void DoDispose() {
        }

    }
}