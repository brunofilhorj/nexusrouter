using System.Reflection;

using Confluent.Kafka;

namespace NexusRouter.Egress.Kafka;

/// <summary>
/// Registra o callback OAUTHBEARER do pacote AWS.MSK.Auth.
/// </summary>
/// <remarks>
/// O assembly não é referência deste projeto. Ele só é carregado quando o mecanismo é
/// <see cref="KafkaSaslMechanism.AwsMskIam"/>. A região sai de <c>AWS_REGION</c> ou <c>AWS_DEFAULT_REGION</c>.
/// O token não é registrado em log.
/// </remarks>
internal static class AwsMskIamTokenRefresh
{
    /// <summary>
    /// Liga o gerador de token IAM ao producer.
    /// </summary>
    /// <param name="builder">Builder ainda não construído.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> é nulo.</exception>
    /// <exception cref="InvalidOperationException">O pacote, a região ou o método do gerador não está disponível.</exception>
    public static void Attach(ProducerBuilder<Null, byte[]> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Assembly auth = LoadRequired("AWS.MSK.Auth");
        Assembly core = LoadRequired("AWSSDK.Core");
        Type generatorType = auth.GetType("AWS.MSK.Auth.AWSMSKAuthTokenGenerator")
            ?? throw new InvalidOperationException("AWS.MSK.Auth não expõe AWSMSKAuthTokenGenerator.");
        Type regionType = core.GetType("Amazon.RegionEndpoint")
            ?? throw new InvalidOperationException("AWSSDK.Core não expõe Amazon.RegionEndpoint.");

        object generator = Activator.CreateInstance(generatorType)
            ?? throw new InvalidOperationException("Não foi possível criar AWSMSKAuthTokenGenerator.");
        MethodInfo generate = FindGenerate(generatorType, regionType);
        object region = ResolveRegion(regionType);

        builder.SetOAuthBearerTokenRefreshHandler((client, _) => Refresh(client, generator, generate, region));
    }

    private static void Refresh(IClient client, object generator, MethodInfo generate, object region)
    {
        try
        {
            object?[] args = new object?[generate.GetParameters().Length];
            args[0] = region;
            ParameterInfo[] parameters = generate.GetParameters();
            for (int i = 1; i < parameters.Length; i++)
            {
                args[i] = parameters[i].HasDefaultValue && parameters[i].DefaultValue is not DBNull
                    ? parameters[i].DefaultValue
                    : throw new InvalidOperationException("GenerateAuthToken tem um parâmetro sem default.");
            }

            object? result = generate.Invoke(generator, args);
            if (result is not System.Runtime.CompilerServices.ITuple tuple
                || tuple.Length < 2
                || tuple[0] is not string token
                || string.IsNullOrEmpty(token)
                || tuple[1] is not long expiry)
            {
                client.OAuthBearerSetTokenFailure("AWS.MSK.Auth não devolveu o par token/expiração.");
                return;
            }

            client.OAuthBearerSetToken(token, expiry, "nexusrouter");
        }
        catch (TargetInvocationException ex)
        {
            client.OAuthBearerSetTokenFailure(ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            client.OAuthBearerSetTokenFailure(ex.Message);
        }
    }

    private static MethodInfo FindGenerate(Type generatorType, Type regionType)
    {
        foreach (MethodInfo method in generatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.Name != "GenerateAuthToken")
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length > 0 && parameters[0].ParameterType == regionType)
            {
                return method;
            }
        }

        throw new InvalidOperationException("AWSMSKAuthTokenGenerator.GenerateAuthToken(RegionEndpoint) não foi encontrado.");
    }

    private static object ResolveRegion(Type regionType)
    {
        string? regionName = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        if (string.IsNullOrWhiteSpace(regionName))
        {
            throw new InvalidOperationException("AwsMskIam exige a variável de ambiente AWS_REGION.");
        }

        MethodInfo getByName = regionType.GetMethod("GetBySystemName", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new InvalidOperationException("RegionEndpoint.GetBySystemName não foi encontrado.");
        return getByName.Invoke(null, [regionName])
            ?? throw new InvalidOperationException("A região AWS informada não foi reconhecida.");
    }

    private static Assembly LoadRequired(string assemblyName)
    {
        try
        {
            return Assembly.Load(assemblyName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new InvalidOperationException(
                $"SaslMechanism AwsMskIam exige o assembly {assemblyName} em tempo de execução.",
                ex);
        }
    }
}
