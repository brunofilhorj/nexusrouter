using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Encapsula um <see cref="TcpClient"/> em TLS 1.2 ou 1.3 quando a sessão pede.
/// </summary>
/// <remarks>
/// A senha do certificado é lida da variável nomeada em <see cref="TlsOptions.CertificatePasswordEnvVar"/>
/// e não é registrada em log. O material da chave privada também não é registrado.
/// </remarks>
public static class TlsSocketFactory
{
    /// <summary>
    /// Devolve o stream da sessão. Sem TLS, é o stream do <paramref name="client"/>.
    /// </summary>
    /// <param name="client">Socket já conectado.</param>
    /// <param name="options">Liga ou desliga o handshake e aponta o certificado cliente.</param>
    /// <param name="cancellationToken">Cancela o handshake.</param>
    /// <returns>Stream para leitura e escrita. Com TLS, o certificado cliente vive até o descarte do stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> ou <paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException"><see cref="TlsOptions.TargetHost"/> é vazio quando TLS está ligado.</exception>
    /// <exception cref="InvalidOperationException">A variável de senha do certificado não está definida.</exception>
    public static async ValueTask<Stream> WrapAsync(TcpClient client, TlsOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return client.GetStream();
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetHost);
        X509Certificate2? certificate = LoadCertificate(options);
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            var authentication = new SslClientAuthenticationOptions
            {
                TargetHost = options.TargetHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };
            if (certificate is not null)
            {
                authentication.ClientCertificates = new X509CertificateCollection { certificate };
            }

            await ssl.AuthenticateAsClientAsync(authentication, cancellationToken).ConfigureAwait(false);
            return new TlsStream(ssl, certificate);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            certificate?.Dispose();
            throw;
        }
    }

    private static X509Certificate2? LoadCertificate(TlsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            return null;
        }

        string? password = null;
        if (!string.IsNullOrWhiteSpace(options.CertificatePasswordEnvVar))
        {
            password = Environment.GetEnvironmentVariable(options.CertificatePasswordEnvVar);
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    $"A variável de ambiente '{options.CertificatePasswordEnvVar}' não está definida.");
            }
        }

        return X509CertificateLoader.LoadPkcs12FromFile(
            options.CertificatePath,
            password,
            X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>
    /// Mantém o certificado cliente vivo enquanto o <see cref="SslStream"/> está em uso.
    /// </summary>
    private sealed class TlsStream : Stream
    {
        private readonly SslStream _ssl;
        private readonly X509Certificate2? _certificate;
        private int _disposed;

        public TlsStream(SslStream ssl, X509Certificate2? certificate)
        {
            _ssl = ssl;
            _certificate = certificate;
        }

        public override bool CanRead => _ssl.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => _ssl.CanWrite;

        public override long Length => _ssl.Length;

        public override long Position
        {
            get => _ssl.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _ssl.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _ssl.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _ssl.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _ssl.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _ssl.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _ssl.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCore();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _ssl.DisposeAsync().ConfigureAwait(false);
                _certificate?.Dispose();
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }

        private void DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ssl.Dispose();
            _certificate?.Dispose();
        }
    }
}
