﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Adapter;
using MQTTnet.Diagnostics;
using MQTTnet.Exceptions;
using MQTTnet.Internal;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace MQTTnet.Server
{
    public class MqttClientSessionPendingMessagesQueue : IDisposable
    {
        private readonly Queue<MqttBasePacket> _queue = new Queue<MqttBasePacket>();
        private readonly AsyncAutoResetEvent _queueLock = new AsyncAutoResetEvent();

        private readonly IMqttServerOptions _options;
        private readonly MqttClientSession _clientSession;
        private readonly IMqttNetChildLogger _logger;

        private long _sentPacketsCount;

        public MqttClientSessionPendingMessagesQueue(IMqttServerOptions options, MqttClientSession clientSession, IMqttNetChildLogger logger)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _clientSession = clientSession ?? throw new ArgumentNullException(nameof(clientSession));

            _logger = logger.CreateChildLogger(nameof(MqttClientSessionPendingMessagesQueue));
        }

        public int Count
        {
            get
            {
                lock (_queue)
                {
                    return _queue.Count;
                }
            }
        }

        public long SentMessagesCount => Interlocked.Read(ref _sentPacketsCount);

        public void Start(IMqttChannelAdapter adapter, CancellationToken cancellationToken)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Task.Run(() => SendQueuedPacketsAsync(adapter, cancellationToken), cancellationToken);
        }

        public void Enqueue(MqttPublishPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));

            lock (_queue)
            {
                if (_queue.Count >= _options.MaxPendingMessagesPerClient)
                {
                    if (_options.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropNewMessage)
                    {
                        return;
                    }

                    if (_options.PendingMessagesOverflowStrategy == MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
                    {
                        _queue.Dequeue();
                    }
                }

                _queue.Enqueue(packet);
            }

            _queueLock.Set();

            _logger.Verbose("Enqueued packet (ClientId: {0}).", _clientSession.ClientId);
        }

        public void Clear()
        {
            lock (_queue)
            {
                _queue.Clear();
            }
        }

        public void Dispose()
        {
        }

        private async Task SendQueuedPacketsAsync(IMqttChannelAdapter adapter, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await TrySendNextQueuedPacketAsync(adapter, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Unhandled exception while sending enqueued packet (ClientId: {0}).", _clientSession.ClientId);
            }
        }

        private async Task TrySendNextQueuedPacketAsync(IMqttChannelAdapter adapter, CancellationToken cancellationToken)
        {
            MqttBasePacket packet = null;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                lock (_queue)
                {
                    if (_queue.Count > 0)
                    {
                        packet = _queue.Dequeue();
                    }
                }

                if (packet == null)
                {
                    await _queueLock.WaitOneAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await adapter.SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);

                _logger.Verbose("Enqueued packet sent (ClientId: {0}).", _clientSession.ClientId);

                Interlocked.Increment(ref _sentPacketsCount);
            }
            catch (Exception exception)
            {
                if (exception is MqttCommunicationTimedOutException)
                {
                    _logger.Warning(exception, "Sending publish packet failed: Timeout (ClientId: {0}).", _clientSession.ClientId);
                }
                else if (exception is MqttCommunicationException)
                {
                    _logger.Warning(exception, "Sending publish packet failed: Communication exception (ClientId: {0}).", _clientSession.ClientId);
                }
                else if (exception is OperationCanceledException)
                {
                }
                else
                {
                    _logger.Error(exception, "Sending publish packet failed (ClientId: {0}).", _clientSession.ClientId);
                }

                if (packet is MqttPublishPacket publishPacket)
                {
                    if (publishPacket.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
                    {
                        publishPacket.Dup = true;

                        Enqueue(publishPacket);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    await _clientSession.StopAsync(MqttClientDisconnectType.NotClean).ConfigureAwait(false);
                }
            }
        }
    }
}