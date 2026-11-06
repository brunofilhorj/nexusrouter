using System.Net.WebSockets;
using DropCopySim.Host.Endpoints;

namespace DropCopySim.Integration.Tests;

public sealed class TelemetryCloseTests
{
    [Fact]
    public async Task TryCloseGracefullyAsync_WhenAborted_DoesNotCloseAndDoesNotThrow()
    {
        var fake = new FakeWebSocket(WebSocketState.Aborted, throwOnClose: false);

        await TelemetryEndpoints.TryCloseGracefullyAsync(fake, default);

        Assert.False(fake.CloseAsyncCalled);
    }

    [Fact]
    public async Task TryCloseGracefullyAsync_WhenOpenAndCloseThrows_SwallowsWebSocketException()
    {
        var fake = new FakeWebSocket(WebSocketState.Open, throwOnClose: true);

        await TelemetryEndpoints.TryCloseGracefullyAsync(fake, default);

        Assert.True(fake.CloseAsyncCalled);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly WebSocketState _state;
        private readonly bool _throwOnClose;

        public FakeWebSocket(WebSocketState state, bool throwOnClose)
        {
            _state = state;
            _throwOnClose = throwOnClose;
        }

        public bool CloseAsyncCalled { get; private set; }

        public override WebSocketState State => _state;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            CloseAsyncCalled = true;
            if (_throwOnClose)
            {
                throw new WebSocketException("Socket aborted.");
            }

            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => throw new NotSupportedException();

        public override string? CloseStatusDescription => throw new NotSupportedException();

        public override string? SubProtocol => throw new NotSupportedException();

        public override void Abort() => throw new NotSupportedException();

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
