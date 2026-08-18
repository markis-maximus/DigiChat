using DigiChat.Domain;
using DigiChat.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DigiChat.Infrastructure.Persistence;

public class DigiChatDbContext(DbContextOptions<DigiChatDbContext> options) : DbContext(options)
{
    public DbSet<Viewer> Viewers => Set<Viewer>();
    public DbSet<Generation> Generations => Set<Generation>();
    public DbSet<Lineage> Lineages => Set<Lineage>();
    public DbSet<DigimonForm> DigimonForms => Set<DigimonForm>();
    public DbSet<ViewerGenerationAssignment> Assignments => Set<ViewerGenerationAssignment>();
    public DbSet<StreamSession> StreamSessions => Set<StreamSession>();
    public DbSet<StreamSessionParticipant> Participants => Set<StreamSessionParticipant>();
    public DbSet<AppState> AppStates => Set<AppState>();
    public DbSet<TransitionRecord> Transitions => Set<TransitionRecord>();
    public DbSet<ProcessedChatEvent> ProcessedChatEvents => Set<ProcessedChatEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Viewer>(e =>
        {
            e.Property(x => x.TwitchUserId).HasMaxLength(32);
            e.Property(x => x.Login).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(128);
            // Twitch user ID is the permanent identity — never login/display name.
            e.HasIndex(x => x.TwitchUserId).IsUnique();
        });

        b.Entity<Generation>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
        });

        b.Entity<Lineage>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.SourceMedia).HasMaxLength(128);
            e.Property(x => x.Canonicality).HasMaxLength(64);
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<DigimonForm>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.AssetKey).HasMaxLength(128);
            e.HasIndex(x => new { x.LineageId, x.Stage }).IsUnique();
            e.HasOne(x => x.Lineage).WithMany(l => l.Forms)
                .HasForeignKey(x => x.LineageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ViewerGenerationAssignment>(e =>
        {
            // One assignment row per viewer per generation…
            e.HasIndex(x => new { x.GenerationId, x.ViewerId }).IsUnique();
            // …and a lineage is held by at most one viewer per generation.
            // Filtered so multiple "awaiting lineage" (NULL) rows can coexist.
            e.HasIndex(x => new { x.GenerationId, x.LineageId }).IsUnique()
                .HasFilter("[LineageId] IS NOT NULL");
            e.HasOne(x => x.Viewer).WithMany(v => v.Assignments)
                .HasForeignKey(x => x.ViewerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Generation).WithMany(g => g.Assignments)
                .HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Lineage).WithMany()
                .HasForeignKey(x => x.LineageId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<StreamSession>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique();
        });

        b.Entity<StreamSessionParticipant>(e =>
        {
            // First qualifying message admits once; the constraint makes
            // duplicate admission impossible even under concurrent delivery.
            e.HasIndex(x => new { x.StreamSessionId, x.ViewerId }).IsUnique();
            e.HasOne(x => x.StreamSession).WithMany(s => s.Participants)
                .HasForeignKey(x => x.StreamSessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Viewer).WithMany()
                .HasForeignKey(x => x.ViewerId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<AppState>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint(
                "CK_AppStates_CurrentStage", "[CurrentStage] >= 0 AND [CurrentStage] <= 4"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.CurrentGeneration).WithMany()
                .HasForeignKey(x => x.CurrentGenerationId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.CurrentStreamSession).WithMany()
                .HasForeignKey(x => x.CurrentStreamSessionId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<TransitionRecord>(e =>
        {
            e.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_Transitions_FromStage", "[FromStage] >= 0 AND [FromStage] <= 4");
                t.HasCheckConstraint(
                    "CK_Transitions_ToStage", "[ToStage] >= 0 AND [ToStage] <= 4");
                t.HasCheckConstraint(
                    "CK_Transitions_Type", "[Type] >= 0 AND [Type] <= 2");
            });
            e.HasIndex(x => x.OccurredUtc);
        });

        b.Entity<ProcessedChatEvent>(e =>
        {
            e.Property(x => x.MessageId).HasMaxLength(64);
            // The idempotency guarantee: a redelivered EventSub message ID
            // violates this index and the duplicate is discarded.
            e.HasIndex(x => x.MessageId).IsUnique();
            e.HasIndex(x => x.ReceivedUtc);
        });
    }
}
