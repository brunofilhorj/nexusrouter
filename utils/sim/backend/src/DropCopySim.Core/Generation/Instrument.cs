using System.ComponentModel.DataAnnotations;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Instrument definition used when generating application messages
/// (SecurityID / Symbol / SecurityExchange).
/// </summary>
public sealed record Instrument
{
    /// <summary>
    /// B3 numeric security identifier written as FIX tag 48 (<c>SecurityID</c>).
    /// </summary>
    [Range(1, long.MaxValue)]
    public long SecurityId { get; init; }

    /// <summary>
    /// Trading symbol written as FIX tag 55 (<c>Symbol</c>), e.g. <c>PETR4</c>.
    /// </summary>
    [Required]
    [MinLength(1)]
    public required string Symbol { get; init; }

    /// <summary>
    /// Market identifier written as FIX tag 207 (<c>SecurityExchange</c>), e.g. <c>BVMF</c>.
    /// </summary>
    [Required]
    [MinLength(1)]
    public required string SecurityExchange { get; init; }
}
