namespace DropCopySim.Core.Generation;

/// <summary>
/// Picks the account and instrument for each simulated order from
/// <see cref="SimulationConfig"/>.
/// </summary>
/// <remarks>
/// Call once per order, then pass the result to <see cref="OrderLifecycleModel"/>.
/// Accounts follow <see cref="SimulationConfig.AccountSelection"/>.
/// Instruments cycle through the configured list in order.
/// Not safe for concurrent calls. <see cref="Resolve"/> does not allocate.
/// </remarks>
public sealed class OrderSelectionResolver
{
    private readonly string[] _accounts;
    private readonly Instrument[] _instruments;
    private readonly AccountSelection _accountSelection;
    private readonly Random _random;
    private int _nextAccount;
    private int _nextInstrument;

    /// <summary>
    /// Creates a resolver over the accounts and instruments in <paramref name="config"/>.
    /// </summary>
    /// <param name="config">Simulation options that supply the lists and the account strategy.</param>
    /// <param name="random">
    /// Source used when <see cref="AccountSelection"/> is <see cref="AccountSelection.Random"/>.
    /// A new <see cref="Random"/> is created when this is null.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The account or instrument list is empty, or an entry is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SimulationConfig.AccountSelection"/> is not a defined strategy.
    /// </exception>
    public OrderSelectionResolver(SimulationConfig config, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.AccountSelection is not (AccountSelection.RoundRobin or AccountSelection.Random))
        {
            throw new ArgumentOutOfRangeException(
                nameof(config),
                config.AccountSelection,
                "AccountSelection must be RoundRobin or Random.");
        }

        _accounts = CopyAccounts(config.Accounts);
        _instruments = CopyInstruments(config.Instruments);
        _accountSelection = config.AccountSelection;
        _random = random ?? new Random();
    }

    /// <summary>
    /// Returns <paramref name="order"/> with the next account and instrument applied.
    /// </summary>
    /// <param name="order">Order template. Identity, quantity, and price fields are preserved.</param>
    /// <returns>
    /// A copy whose <see cref="OrderContext.Account"/>, <see cref="OrderContext.SecurityId"/>,
    /// <see cref="OrderContext.Symbol"/>, and <see cref="OrderContext.SecurityExchange"/>
    /// come from the configured lists.
    /// </returns>
    public OrderContext Resolve(in OrderContext order)
    {
        var instrument = NextInstrument();
        return order with
        {
            Account = NextAccount(),
            SecurityId = instrument.SecurityId,
            Symbol = instrument.Symbol,
            SecurityExchange = instrument.SecurityExchange,
        };
    }

    private string NextAccount()
    {
        if (_accountSelection == AccountSelection.Random)
        {
            return _accounts[_random.Next(_accounts.Length)];
        }

        var index = _nextAccount;
        _nextAccount = index + 1 == _accounts.Length ? 0 : index + 1;
        return _accounts[index];
    }

    private Instrument NextInstrument()
    {
        var index = _nextInstrument;
        _nextInstrument = index + 1 == _instruments.Length ? 0 : index + 1;
        return _instruments[index];
    }

    private static string[] CopyAccounts(IReadOnlyList<string> accounts)
    {
        if (accounts.Count == 0)
        {
            throw new ArgumentException("At least one account is required.", nameof(accounts));
        }

        var copy = new string[accounts.Count];
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            if (string.IsNullOrWhiteSpace(account))
            {
                throw new ArgumentException($"Accounts[{i}] must be a non-empty account identifier.", nameof(accounts));
            }

            copy[i] = account;
        }

        return copy;
    }

    private static Instrument[] CopyInstruments(IReadOnlyList<Instrument> instruments)
    {
        if (instruments.Count == 0)
        {
            throw new ArgumentException("At least one instrument is required.", nameof(instruments));
        }

        var copy = new Instrument[instruments.Count];
        for (var i = 0; i < instruments.Count; i++)
        {
            var instrument = instruments[i];
            if (instrument is null
                || instrument.SecurityId <= 0
                || string.IsNullOrWhiteSpace(instrument.Symbol)
                || string.IsNullOrWhiteSpace(instrument.SecurityExchange))
            {
                throw new ArgumentException(
                    $"Instruments[{i}] must have a positive SecurityId, Symbol, and SecurityExchange.",
                    nameof(instruments));
            }

            copy[i] = instrument;
        }

        return copy;
    }
}
