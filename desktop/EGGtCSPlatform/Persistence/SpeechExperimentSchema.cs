using Microsoft.EntityFrameworkCore;

namespace EGGtCSPlatform.Persistence;

internal static class SpeechExperimentSchema
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SpeechExperimentEntity>(e =>
        {
            e.ToTable("SpeechExperiments");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceJson).IsRequired();
            e.HasOne(x => x.Experiment)
                .WithOne(x => x.SpeechExperiment)
                .HasForeignKey<SpeechExperimentEntity>(x => x.ExperimentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SpeechTrainingRoundEntity>(e =>
        {
            e.ToTable("SpeechTrainingRounds");
            e.HasKey(x => x.Id);
            e.Property(x => x.ResultJson).IsRequired();
            e.HasIndex(x => new { x.SpeechExperimentId, x.Number }).IsUnique();
            e.HasOne(x => x.Experiment)
                .WithMany(x => x.Rounds)
                .HasForeignKey(x => x.SpeechExperimentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
