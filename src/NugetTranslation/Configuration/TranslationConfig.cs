using Microsoft.Extensions.Configuration;
using Serilog.Events;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace NugetTranslation.Configuration;

internal enum ConfigSourceKind { Constant, Env, Secrets }

[JsonDerivedType(typeof(ConstantSource), "constant")]
[JsonDerivedType(typeof(EnvSource), "env")]
[JsonDerivedType(typeof(SecretsSource), "secrets")]
internal abstract record AIConfigSource
{
    public abstract ConfigSourceKind Kind { get; }
    public abstract string Resolve();
}

internal sealed record ConstantSource(string Value) : AIConfigSource
{
    public override ConfigSourceKind Kind => ConfigSourceKind.Constant;
    public override string Resolve()
    {
        return Value;
    }
}

internal sealed record EnvSource(string Value) : AIConfigSource
{
    public override ConfigSourceKind Kind => ConfigSourceKind.Env;
    public override string Resolve()
    {
        return Environment.GetEnvironmentVariable(Value)
               ?? throw new InvalidOperationException($"环境变量 {Value} 未设置");
    }
}

internal sealed record SecretsSource(string Value) : AIConfigSource
{
    public override ConfigSourceKind Kind => ConfigSourceKind.Secrets;
    public override string Resolve()
    {
        return new ConfigurationBuilder()
                .AddUserSecrets<Program>()
                .Build()[Value]
               ?? throw new InvalidOperationException($"UserSecrets 中未找到 \"{Value}\"");
    }
}

internal sealed record AIConfig(
    AIConfigSource? Url,
    AIConfigSource? Model,
    AIConfigSource? Key)
{
    [MemberNotNull(nameof(Url), nameof(Model), nameof(Key))]
    public void EnsureResolved()
    {
        _ = Url ?? throw new InvalidOperationException("API 地址未配置");
        _ = Model ?? throw new InvalidOperationException("模型未配置");
        _ = Key ?? throw new InvalidOperationException("API Key 未配置");
    }
}

internal sealed record ProfileConfig(
    string? BaseDir,
    LogEventLevel? LogLevel = LogEventLevel.Information,
    AIConfig? Ai = null
);
