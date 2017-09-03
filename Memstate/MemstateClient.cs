using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Memstate.Tcp;
using Microsoft.Extensions.Logging;

namespace Memstate
{
    public class MemstateClient<TModel> : Client<TModel> where TModel : class
    {
        private readonly Config _config;
        private readonly ILogger _logger;

        private TcpClient _tcpClient;
        private readonly ISerializer _serializer;
        private NetworkStream _stream;

        private readonly Dictionary<Guid, TaskCompletionSource<NetworkMessage>> _pendingRequests;
        private MessageProcessor<NetworkMessage> _messageWriter;
        private Task _messageReader;
        

        private readonly Counter _counter = new Counter();
        private readonly CancellationTokenSource _cancellationSource;

        public MemstateClient(Config config)
        {
            _config = config;
            _serializer = config.GetSerializer();
            _pendingRequests = new Dictionary<Guid, TaskCompletionSource<NetworkMessage>>();
            _logger = _config.LoggerFactory.CreateLogger<MemstateClient<TModel>>();
            _cancellationSource = new CancellationTokenSource();
        }

        public async Task ConnectAsync(string host = "localhost", int port = 3001)
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);
            _stream = _tcpClient.GetStream();
            _logger.LogInformation($"Connected to {host}:{port}");
            _messageWriter = new MessageProcessor<NetworkMessage>(WriteMessage);
            _messageReader = Task.Run(ReceiveMessages);
        }

        private void Handle(NetworkMessage message)
        {
            var messageType = message.GetType();
            try
            {
                var methodInfo = GetType().GetRuntimeMethod("Handle", new[] {messageType});
                if (methodInfo == null) _logger.LogError("No handler for message of type " + messageType.Name);
                else methodInfo.Invoke(this, new object[] {message});
            }
            catch (TargetException ex)
            {
                _logger.LogError( ex, $"Handler for {messageType.Name} failed");
            }
        }

        private void Handle(QueryResponse response)
        {
            var requestId = response.ResponseTo;
            CompleteRequest(requestId, response);
        }

        private void CompleteRequest(Guid requestId, Response response)
        {
            var completionSource = _pendingRequests[requestId];
            completionSource?.SetResult(response);

            if (!_pendingRequests.Remove(requestId))
            {
                _logger.LogError($"No completion source for {response}, id {response.ResponseTo}");
            }

        }

        private async Task ReceiveMessages()
        {
            var serializer = _config.GetSerializer();
            var cancellationToken = _cancellationSource.Token;
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await NetworkMessage.ReadAsync(_stream, serializer, cancellationToken);
                if (message == null) break;
                Handle(message);
            }
        }

        /// <summary>
        /// This method is called by the message processor never call it directly!
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        private async Task WriteMessage(NetworkMessage message)
        {
            _logger.LogDebug("WriteMessage: invoked with " + message);
            var bytes = _serializer.Serialize(message);
            _logger.LogDebug("WriteMessage: serialized message size: " + bytes.Length);
            var messageId = _counter.Next();
            var packet = Packet.Create(bytes, messageId);
            await packet.WriteTo(_stream);
            _logger.LogTrace("Packet written");
            await _stream.FlushAsync();
        }

        private async Task<NetworkMessage> SendAndReceive(Request request)
        {
            var completionSource = new TaskCompletionSource<NetworkMessage>();
            _pendingRequests[request.Id] = completionSource;
            _messageWriter.Enqueue(request);
            return await completionSource.Task;
        }

        internal override object Execute(Query query)
        {
            throw new NotImplementedException();
        }

        public override void Execute(Command<TModel> command)
        {
            throw new System.NotImplementedException();
        }

        public override TResult Execute<TResult>(Command<TModel, TResult> command)
        {
            throw new System.NotImplementedException();
        }

        public override TResult Execute<TResult>(Query<TModel, TResult> query)
        {
            return ExecuteAsync(query).Result;
        }


        public override Task ExecuteAsync(Command<TModel> command)
        {
            throw new System.NotImplementedException();
        }

        public override async Task<TResult> ExecuteAsync<TResult>(Command<TModel, TResult> command)
        {
            var request = new CommandRequest(command);
            var response = (CommandResponse) await SendAndReceive(request);
            return (TResult) response.Result;
        }

        public override async Task<TResult> ExecuteAsync<TResult>(Query<TModel, TResult> query)
        {
            var request = new QueryRequest(query);
            var response = (QueryResponse) await SendAndReceive(request);
            return (TResult) response.Result;
        }

        public void Dispose()
        {
            _messageWriter.Dispose();
            _tcpClient.Dispose();
        }
    }
}