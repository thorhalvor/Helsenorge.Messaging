﻿/* 
 * Copyright (c) 2020, Norsk Helsenett SF and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the MIT license
 * available at https://raw.githubusercontent.com/helsenorge/Helsenorge.Messaging/master/LICENSE
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Helsenorge.Messaging.Abstractions;
using Helsenorge.Messaging.Amqp;
using Helsenorge.Messaging.Amqp.Receivers;
using Helsenorge.Registries.Abstractions;
using Microsoft.Extensions.Logging;

namespace Helsenorge.Messaging
{
    public record QueueNames
    {
        public string Async { get; set; }
        public string Sync { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Default implementation for <see cref="IMessagingServer"/>
    /// This must be hosted as a singleton in order to leverage connection pooling against the message bus
    /// </summary>
    public class MessagingServer : MessagingCore, IMessagingServer, IMessagingNotification, IAsyncDisposable
    {
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentBag<MessageListener> _listeners = new ConcurrentBag<MessageListener>();
        private readonly ConcurrentBag<Task> _tasks = new ConcurrentBag<Task>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private bool _disposed = false;

        private Action<IncomingMessage> _onAsynchronousMessageReceived;
        private Action<MessageListener, IncomingMessage> _onAsynchronousMessageReceivedStarting;
        private Action<IncomingMessage> _onAsynchronousMessageReceivedCompleted;
        private Func<IncomingMessage, Task> _onAsynchronousMessageReceivedAsync;
        private Func<MessageListener, IncomingMessage, Task> _onAsynchronousMessageReceivedStartingAsync;
        private Func<IncomingMessage, Task> _onAsynchronousMessageReceivedCompletedAsync;

        private Func<IncomingMessage, XDocument> _onSynchronousMessageReceived;
        private Action<IncomingMessage> _onSynchronousMessageReceivedCompleted;
        private Action<IncomingMessage> _onSynchronousMessageReceivedStarting;
        private Func<IncomingMessage, Task<XDocument>> _onSynchronousMessageReceivedAsync;
        private Func<IncomingMessage, Task> _onSynchronousMessageReceivedCompletedAsync;
        private Func<IncomingMessage, Task> _onSynchronousMessageReceivedStartingAsync;

        private Action<IMessagingMessage> _onErrorMessageReceived;
        private Action<IncomingMessage> _onErrorMessageReceivedStarting;
        private Func<IMessagingMessage, Task> _onErrorMessageReceivedAsync;
        private Func<IncomingMessage, Task> _onErrorMessageReceivedStartingAsync;


        private Action<IMessagingMessage, Exception> _onUnhandledException;
        private Action<IMessagingMessage, Exception> _onHandledException;
        private Func<IMessagingMessage, Exception, Task> _onUnhandledExceptionAsync;
        private Func<IMessagingMessage, Exception, Task> _onHandledExceptionAsync;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="settings">Set of options to use</param>
        /// <param name="loggerFactory">Logger Factory</param>
        /// <param name="collaborationProtocolRegistry">Reference to the collaboration protocol registry</param>
        /// <param name="addressRegistry">Reference to the address registry</param>
        public MessagingServer(
            MessagingSettings settings,
            ILoggerFactory loggerFactory,
            ICollaborationProtocolRegistry collaborationProtocolRegistry,
            IAddressRegistry addressRegistry) : base(settings, collaborationProtocolRegistry, addressRegistry)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = _loggerFactory.CreateLogger(nameof(MessagingServer));
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="settings">Set of options to use</param>
        /// <param name="loggerFactory">Logger Factory</param>
        /// <param name="collaborationProtocolRegistry">Reference to the collaboration protocol registry</param>
        /// <param name="addressRegistry">Reference to the address registry</param>
        /// <param name="certificateStore">Reference to an implementation of <see cref="ICertificateStore"/>.</param>
        public MessagingServer(
            MessagingSettings settings,
            ILoggerFactory loggerFactory,
            ICollaborationProtocolRegistry collaborationProtocolRegistry,
            IAddressRegistry addressRegistry,
            ICertificateStore certificateStore) : base(settings, collaborationProtocolRegistry, addressRegistry, certificateStore)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = _loggerFactory.CreateLogger(nameof(MessagingServer));
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="settings">Set of options to use</param>
        /// <param name="loggerFactory">Logger Factory</param>
        /// <param name="collaborationProtocolRegistry">Reference to the collaboration protocol registry</param>
        /// <param name="addressRegistry">Reference to the address registry</param>
        /// <param name="certificateStore">Reference to an implementation of <see cref="ICertificateStore"/>.</param>
        /// <param name="certificateValidator">Reference to an implementation of <see cref="ICertificateValidator"/>.</param>
        /// <param name="messageProtection">Reference to an implementation of <see cref="IMessageProtection"/>.</param>
        public MessagingServer(
            MessagingSettings settings,
            ILoggerFactory loggerFactory,
            ICollaborationProtocolRegistry collaborationProtocolRegistry,
            IAddressRegistry addressRegistry,
            ICertificateStore certificateStore,
            ICertificateValidator certificateValidator,
            IMessageProtection messageProtection) : base(settings, collaborationProtocolRegistry, addressRegistry, certificateStore, certificateValidator, messageProtection)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _logger = _loggerFactory.CreateLogger(nameof(MessagingServer));
        }

        /// <summary>
        /// Returns the Last Read Time from the asynchronous queue in UTC format.
        /// </summary>
        public DateTime? AsynchronousQueueLastReadTimeUtc =>
            _listeners.Where(listener => listener is AsynchronousMessageListener)
                .OrderByDescending(listener => listener.LastReadTimeUtc)
                .FirstOrDefault()?.LastReadTimeUtc;
        /// <summary>
        /// Returns the Last Read Time from the synchronous queue in UTC format.
        /// </summary>
        public DateTime? SynchronousQueueLastReadTimeUtc =>
            _listeners.Where(listener => listener is SynchronousMessageListener)
                .OrderByDescending(listener => listener.LastReadTimeUtc)
                .FirstOrDefault()?.LastReadTimeUtc;
        /// <summary>
        /// Returns the Last Read Time from the error queue in UTC format.
        /// </summary>
        public DateTime? ErrorQueueLastReadTimeUtc =>
            _listeners.Where(listener => listener is ErrorMessageListener)
                .OrderByDescending(listener => listener.LastReadTimeUtc)
                .FirstOrDefault()?.LastReadTimeUtc;

        /// <summary>
        /// Start the server
        /// </summary>
        public async Task StartAsync()
        {
            _logger.LogInformation("Messaging Server starting up");

            if (!await CanAuthenticateAgainstMessageBrokerAsync())
                throw new MessagingException("Non-successful authentication or connection attempt to the message broker on start-up. This can be caused by incorrect credentials / configuration errors.") { EventId = EventIds.ConnectionToMessageBrokerFailed };

            if (!await HasCommonAncestorAsync(BusCore.Settings.MyHerIds.ToArray()))
                throw new MessagingException("There must be a common set of ancestor queues when receiving from multiple HER-Ids.");

            var queueNames = await GetCommonAncestorAsync(BusCore.Settings.MyHerIds);

            for (var i = 0; i < Settings.BusSettings.Asynchronous.ProcessingTasks; i++)
            {
                _listeners.Add(new AsynchronousMessageListener(BusCore, _loggerFactory.CreateLogger($"AsyncListener_{i}"), this, queueNames));
            }
            for (var i = 0; i < Settings.BusSettings.Synchronous.ProcessingTasks; i++)
            {
                _listeners.Add(new SynchronousMessageListener(BusCore, _loggerFactory.CreateLogger($"SyncListener_{i}"), this, queueNames));
            }
            for (var i = 0; i < Settings.BusSettings.Error.ProcessingTasks; i++)
            {
                _listeners.Add(new ErrorMessageListener(BusCore, _loggerFactory.CreateLogger($"ErrorListener_{i}"), this, queueNames));
            }
            foreach (var listener in _listeners)
            {
                _tasks.Add(Task.Run(async () => await listener.StartAsync(_cancellationTokenSource.Token).ConfigureAwait(false)));
            }
        }

        /// <summary>
        /// Stops the server, waiting for all tasks to complete their current work
        /// </summary>
        public Task StopAsync()
        {
            return StopAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// Stops the server, waiting for all tasks to complete their current work
        /// </summary>
        /// <param name="timeout">The amount of time we wait for things to shut down</param>
        public async Task StopAsync(TimeSpan timeout)
        {
            _logger.LogInformation("Messaging Server shutting down");

            _cancellationTokenSource.Cancel();
            await Task.WhenAll(_tasks.ToArray()).ConfigureAwait(false);
            
            // when all the listeners have shut down, close down the messaging infrastructure
            await BusCore.SenderPool.ShutdownAsync(_logger).ConfigureAwait(false);
            await BusCore.ReceiverPool.ShutdownAsync(_logger).ConfigureAwait(false);
            await BusCore.FactoryPool.ShutdownAsync(_logger).ConfigureAwait(false);
        }

        /// <summary>
        /// Registers a delegate that should be called when we have enough information to process the message. This is where the main processing logic hooks in.
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterAsynchronousMessageReceivedCallback(Action<IncomingMessage> action) => _onAsynchronousMessageReceived = action;
        /// <summary>
        /// Registers a delegate that should be called when we have enough information to process the message. This is where the main processing logic hooks in.
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterAsynchronousMessageReceivedCallbackAsync(Func<IncomingMessage, Task> action) => _onAsynchronousMessageReceivedAsync = action;

        async Task IMessagingNotification.NotifyAsynchronousMessageReceivedAsync(IncomingMessage message)
        {
            _logger.LogDebug("NotifyAsynchronousMessageReceived");
            if (_onAsynchronousMessageReceivedAsync != null)
            {
                await _onAsynchronousMessageReceivedAsync.Invoke(message).ConfigureAwait(false);
            }
            if (_onAsynchronousMessageReceived != null)
            {
                _onAsynchronousMessageReceived.Invoke(message);
            }
        }

        /// <summary>
        /// Registers a delegate that should be called as we start processing a message
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterAsynchronousMessageReceivedStartingCallback(Action<MessageListener, IncomingMessage> action) => _onAsynchronousMessageReceivedStarting = action;
        /// <summary>
        /// Registers a delegate that should be called as we start processing a message
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterAsynchronousMessageReceivedStartingCallbackAsync(Func<MessageListener, IncomingMessage, Task> action) => _onAsynchronousMessageReceivedStartingAsync = action;

        async Task IMessagingNotification.NotifyAsynchronousMessageReceivedStartingAsync(MessageListener listener, IncomingMessage message)
        {
            _logger.LogDebug("NotifyAsynchronousMessageReceivedStarting");
            if (_onAsynchronousMessageReceivedStartingAsync != null)
            {
                await _onAsynchronousMessageReceivedStartingAsync.Invoke(listener, message).ConfigureAwait(false);
            }
            if (_onAsynchronousMessageReceivedStarting != null)
            {
                _onAsynchronousMessageReceivedStarting.Invoke(listener, message);
            }
        }

        /// <summary>
        /// Registers a delegate that should be called when we are finished processing the message.
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterAsynchronousMessageReceivedCompletedCallback(Action<IncomingMessage> action) => _onAsynchronousMessageReceivedCompleted = action;
        /// <summary>
        /// Registers a delegate that should be called when we are finished processing the message.
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterAsynchronousMessageReceivedCompletedCallbackAsync(Func<IncomingMessage, Task> action) => _onAsynchronousMessageReceivedCompletedAsync = action;

        async Task IMessagingNotification.NotifyAsynchronousMessageReceivedCompletedAsync(IncomingMessage message)
        {
            _logger.LogDebug("NotifyAsynchronousMessageReceivedCompleted");
            if (_onAsynchronousMessageReceivedCompletedAsync != null)
            {
                await _onAsynchronousMessageReceivedCompletedAsync.Invoke(message).ConfigureAwait(false);
            }
            if (_onAsynchronousMessageReceivedCompleted != null)
            {
                _onAsynchronousMessageReceivedCompleted.Invoke(message);
            }
        }
        /// <summary>
        /// Registers a delegate that should be called when we receive an error message
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterErrorMessageReceivedCallback(Action<IMessagingMessage> action) => _onErrorMessageReceived = action;
        /// <summary>
        /// Registers a delegate that should be called when we receive an error message
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterErrorMessageReceivedCallbackAsync(Func<IMessagingMessage, Task> action) => _onErrorMessageReceivedAsync = action;

        async Task IMessagingNotification.NotifyErrorMessageReceivedAsync(IMessagingMessage message)
        {
            _logger.LogDebug("NotifyErrorMessageReceived");
            if (_onErrorMessageReceivedAsync != null)
            {
                await _onErrorMessageReceivedAsync.Invoke(message).ConfigureAwait(false);
            }
            if (_onErrorMessageReceived != null)
            {
                _onErrorMessageReceived.Invoke(message);
            }
        }

        /// <summary>
        /// Registers a delegate that should be called as we start processing a message
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterErrorMessageReceivedStartingCallback(Action<IncomingMessage> action) => _onErrorMessageReceivedStarting = action;
        /// <summary>
        /// Registers a delegate that should be called as we start processing a message
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterErrorMessageReceivedStartingCallback(Func<IncomingMessage, Task> action) => _onErrorMessageReceivedStartingAsync = action;

        async Task IMessagingNotification.NotifyErrorMessageReceivedStartingAsync(IncomingMessage message)
        {
            _logger.LogDebug("NotifyErrorMessageReceivedStarting");
            if (_onErrorMessageReceivedStartingAsync != null)
            {
                await _onErrorMessageReceivedStartingAsync.Invoke(message).ConfigureAwait(false);
            }
            if (_onErrorMessageReceivedStarting != null)
            {
                _onErrorMessageReceivedStarting.Invoke(message);
            }
        }

        /// <summary>
        /// Registers a delegate that should be called when we have enough information to process the message. This is where the main processing logic hooks in.
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterSynchronousMessageReceivedCallback(Func<IncomingMessage, XDocument> action) => _onSynchronousMessageReceived = action;
        /// <summary>
        /// Registers a delegate that should be called when we have enough information to process the message. This is where the main processing logic hooks in.
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterSynchronousMessageReceivedCallbackAsync(Func<IncomingMessage, Task<XDocument>> action) => _onSynchronousMessageReceivedAsync = action;

        async Task<XDocument> IMessagingNotification.NotifySynchronousMessageReceivedAsync(IncomingMessage message)
        {
            _logger.LogDebug("NotifySynchronousMessageReceived");
            if (_onSynchronousMessageReceivedAsync != null)
            {
                return await _onSynchronousMessageReceivedAsync.Invoke(message).ConfigureAwait(false);
            }
            if (_onSynchronousMessageReceived != null)
            {
                return _onSynchronousMessageReceived.Invoke(message);
            }

            return default;
        }
        /// <summary>
        /// Registers a delegate that should be called when we are finished processing the message.
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterSynchronousMessageReceivedCompletedCallback(Action<IncomingMessage> action) => _onSynchronousMessageReceivedCompleted = action;
        /// <summary>
        /// Registers a delegate that should be called when we are finished processing the message.
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterSynchronousMessageReceivedCompletedCallbackAsync(Func<IncomingMessage, Task> action) => _onSynchronousMessageReceivedCompletedAsync = action;

        async Task IMessagingNotification.NotifySynchronousMessageReceivedCompletedAsync(IncomingMessage message)
        {
            _logger.LogDebug("NotifySynchronousMessageReceivedCompleted");
            if (_onSynchronousMessageReceivedCompletedAsync != null)
            {
                await _onSynchronousMessageReceivedCompletedAsync.Invoke(message).ConfigureAwait(false);
            }
            if (_onSynchronousMessageReceivedCompleted != null)
            {
                _onSynchronousMessageReceivedCompleted.Invoke(message);
            }
        }
        /// <summary>
        /// Registers a delegate that should be called as we start processing a message
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterSynchronousMessageReceivedStartingCallback(Action<IncomingMessage> action) => _onSynchronousMessageReceivedStarting = action;
        /// <summary>
        /// Registers a delegate that should be called as we start processing a message
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterSynchronousMessageReceivedStartingCallbackAsync(Func<IncomingMessage, Task> action) => _onSynchronousMessageReceivedStartingAsync = action;

        async Task IMessagingNotification.NotifySynchronousMessageReceivedStartingAsync(IncomingMessage message)
        {
            _logger.LogDebug("NotifySynchronousMessageReceivedStarting");
            if (_onSynchronousMessageReceivedStartingAsync != null)
            {
                await _onSynchronousMessageReceivedStartingAsync.Invoke(message).ConfigureAwait(false);
            }
            if (_onSynchronousMessageReceivedStarting != null)
            {
                _onSynchronousMessageReceivedStarting.Invoke(message);
            }
        }
        /// <summary>
        /// Registers a delegate that should be called when we have an handled exception
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterHandledExceptionCallback(Action<IMessagingMessage, Exception> action) => _onHandledException = action;
        /// <summary>
        /// Registers a delegate that should be called when we have an handled exception
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterHandledExceptionCallbackAsync(Func<IMessagingMessage, Exception, Task> action) => _onHandledExceptionAsync = action;

        async Task IMessagingNotification.NotifyHandledExceptionAsync(IMessagingMessage message, Exception ex)
        {
            _logger.LogDebug("NotifyHandledException");
            if (_onHandledExceptionAsync != null)
            {
                await _onHandledExceptionAsync.Invoke(message, ex).ConfigureAwait(false);
            }
            if (_onHandledException != null)
            {
                _onHandledException.Invoke(message, ex);
            }
        }
        /// <summary>
        /// Registers a delegate that should be called when we have an unhandled exception
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterUnhandledExceptionCallback(Action<IMessagingMessage, Exception> action) => _onUnhandledException = action;
        /// <summary>
        /// Registers a delegate that should be called when we have an unhandled exception
        /// </summary>
        /// <param name="action">The delegate that should be called</param>
        public void RegisterUnhandledExceptionCallbackAsync(Func<IMessagingMessage, Exception, Task> action) => _onUnhandledExceptionAsync = action;

        async Task IMessagingNotification.NotifyUnhandledExceptionAsync(IMessagingMessage message, Exception ex)
        {
            _logger.LogDebug("NotifyUnhandledException");
            if (_onUnhandledExceptionAsync != null)
            {
                await _onUnhandledExceptionAsync.Invoke(message, ex).ConfigureAwait(false);
            }
            if(_onUnhandledException != null)
            {
                _onUnhandledException.Invoke(message, ex);
            }
        }

        private async Task<bool> HasCommonAncestorAsync(int[] herIds)
        {
            if (herIds.Count() <= 1)
                return true;

            var communicationPartyDetailsList = new List<CommunicationPartyDetails>();
            foreach (var herId in herIds)
                communicationPartyDetailsList.Add(await BusCore.AddressRegistry.FindCommunicationPartyDetailsAsync(herId));

            var queueNames = GetCommonAncestor(communicationPartyDetailsList);

            foreach (var communicationPartyDetails in communicationPartyDetailsList)
            {
                if (!string.IsNullOrEmpty(queueNames.Async) &&
                    !string.IsNullOrEmpty(communicationPartyDetails.AsynchronousQueueName))
                {
                    if (!queueNames.Async.Equals(communicationPartyDetails.AsynchronousQueueName,
                            StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                if (!string.IsNullOrEmpty(queueNames.Sync) &&
                    !string.IsNullOrEmpty(communicationPartyDetails.SynchronousQueueName))
                {
                    if (!queueNames.Sync.Equals(communicationPartyDetails.SynchronousQueueName,
                            StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                if (!string.IsNullOrEmpty(queueNames.Error) &&
                    !string.IsNullOrEmpty(communicationPartyDetails.ErrorQueueName))
                {
                    if (!queueNames.Error.Equals(communicationPartyDetails.ErrorQueueName,
                            StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            return true;
        }

        private async Task<QueueNames> GetCommonAncestorAsync(IEnumerable<int> herIds)
        {
            var communicationPartyDetailsList = new List<CommunicationPartyDetails>();
            foreach (var herId in herIds)
                communicationPartyDetailsList.Add(await BusCore.AddressRegistry.FindCommunicationPartyDetailsAsync(herId));
            return GetCommonAncestor(communicationPartyDetailsList);
        }

        private QueueNames GetCommonAncestor(IReadOnlyList<CommunicationPartyDetails> communicationPartyDetailsList)
        {
            return new QueueNames
            {
                Async = communicationPartyDetailsList
                    .FirstOrDefault(c => !string.IsNullOrEmpty(c.AsynchronousQueueName))?.AsynchronousQueueName,
                Sync = communicationPartyDetailsList
                    .FirstOrDefault(c => !string.IsNullOrEmpty(c.SynchronousQueueName))?.SynchronousQueueName,
                Error = communicationPartyDetailsList
                    .FirstOrDefault(c => !string.IsNullOrEmpty(c.ErrorQueueName))?.ErrorQueueName,
            };
        }

        /// <summary>
        /// Tries to authenticate against the message broker.
        /// </summary>
        /// <returns>true if authentication is successful, otherwise false.</returns>
        protected virtual async Task<bool> CanAuthenticateAgainstMessageBrokerAsync()
        {
            AmqpConnection connection = null;
            try
            {
                connection = new AmqpConnection(Settings.BusSettings.ConnectionString?.ToString());
                _ = await connection.EnsureConnectionAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(EventIds.ConnectionToWebServiceFailed, e, "Non-successful connection and ping attempt to the Message Broker Service. This can be caused by incorrect credentials / configuration errors.");

                return false;
            }
            finally
            {
                if (connection != null) await connection.CloseAsync();
            }

            return true;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;

            await StopAsync(TimeSpan.Zero).ConfigureAwait(false);

            _disposed = true;
        }
    }
}
