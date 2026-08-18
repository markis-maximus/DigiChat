using DigiChat.Infrastructure.Services;

namespace DigiChat.Infrastructure.Twitch;

/// <summary>
/// Mutable connection-status holder, shared between the EventSub service
/// (writer) and the admin status projection (reader). A separate class breaks
/// what would otherwise be a circular DI dependency.
/// </summary>
public class TwitchStatus : ITwitchStatusProvider
{
    private volatile string _status = "Not started";

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }

    public event Action<string>? StatusChanged;
}
