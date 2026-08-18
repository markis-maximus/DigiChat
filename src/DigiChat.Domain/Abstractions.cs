using DigiChat.Domain.Views;

namespace DigiChat.Domain;

/// <summary>Injectable clock so time-dependent logic is testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public enum AdmissionOutcome
{
    /// <summary>First qualifying message this session — Digimon spawns.</summary>
    Admitted = 0,
    /// <summary>Already a participant; no visual event (spec §6).</summary>
    AlreadyParticipant = 1,
    /// <summary>EventSub redelivery detected via message ID; fully ignored.</summary>
    DuplicateEvent = 2,
    /// <summary>No stream session is active; chat cannot admit anyone.</summary>
    NoActiveSession = 3,
    /// <summary>Configured ignored user (bots) or shared-chat message from another channel.</summary>
    Ignored = 4,
    /// <summary>The configured per-session safety cap has been reached.</summary>
    CapacityReached = 5,
}

public sealed record AdmissionResult(AdmissionOutcome Outcome, ParticipantView? Participant)
{
    public static readonly AdmissionResult Duplicate = new(AdmissionOutcome.DuplicateEvent, null);
    public static readonly AdmissionResult NoSession = new(AdmissionOutcome.NoActiveSession, null);
    public static readonly AdmissionResult IgnoredUser = new(AdmissionOutcome.Ignored, null);
    public static readonly AdmissionResult AtCapacity = new(AdmissionOutcome.CapacityReached, null);
}

/// <summary>An inbound chat message, normalized from EventSub or the mock simulator.</summary>
public sealed record ChatMessageEvent(
    string MessageId,
    string TwitchUserId,
    string Login,
    string DisplayName,
    /// <summary>True when the message originated in another channel via Shared Chat.</summary>
    bool IsFromOtherChannel);
