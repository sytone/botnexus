namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// The single implementation of "set these environment variables, run this, put them back".
/// It previously existed as an identical private <c>WithEnv</c> in both
/// <c>EnvironmentApiKeysTests</c> and <c>ProviderCredentialResolverTests</c>; two copies of a
/// process-global mutation are two places for the restore semantics to drift, so #3151 collapsed
/// them here.
/// </summary>
/// <remarks>
/// Callers MUST join <see cref="EnvironmentVariableCollection"/> — this helper restores prior
/// values but cannot defend against a concurrently running collection observing the window in
/// between.
/// </remarks>
internal static class EnvironmentVariableScope
{
    /// <summary>
    /// Sets <paramref name="vars"/> for the duration of <paramref name="action"/>, then restores
    /// their prior values so tests do not leak process-wide environment state.
    /// </summary>
    /// <param name="vars">Variables to set; a <c>null</c> value unsets the variable.</param>
    /// <param name="action">The body to run with those variables in force.</param>
    /// <param name="reset">
    /// Optional hook invoked both immediately before <paramref name="action"/> and after the
    /// variables are restored. <c>ProviderCredentialResolverTests</c> uses it to clear
    /// <c>ProviderCredentialResolver</c>'s warn-once ambient state, which is static and would
    /// otherwise carry across tests; that behaviour is preserved rather than dropped.
    /// </param>
    public static void WithEnv(Dictionary<string, string?> vars, Action action, Action? reset = null)
    {
        var prior = new Dictionary<string, string?>();
        foreach (var (key, value) in vars)
        {
            prior[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            reset?.Invoke();
            action();
        }
        finally
        {
            foreach (var (key, value) in prior)
                Environment.SetEnvironmentVariable(key, value);
            reset?.Invoke();
        }
    }
}
