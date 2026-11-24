namespace ChestCounter;

public static class RemoteProfileUrl
{
    public const string PlayersUrl = "https://contadordebau.fortcards.me/jogadores";
    public const string BaseUrl = PlayersUrl + "/";

    public static bool TryBuild(long? playerId, out Uri? uri, out string error)
    {
        uri = null;
        error = "";
        if (playerId is not > 0)
        {
            error = "The FortCards player profile is not available yet.";
            return false;
        }

        if (!Uri.TryCreate(BaseUrl + playerId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), UriKind.Absolute, out var parsed))
        {
            error = "The remote player profile URL is invalid.";
            return false;
        }

        uri = parsed;
        return true;
    }
}
