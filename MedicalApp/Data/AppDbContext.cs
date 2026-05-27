using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; } = null!;
        public DbSet<InterpretationHistory> InterpretationHistories { get; set; } = null!;
        public DbSet<Purchase> Purchases { get; set; } = null!;
        public DbSet<PromoCode> PromoCodes { get; set; } = null!;
        public DbSet<Profile> Profiles { get; set; } = null!;
        public DbSet<LoincEntry> LoincDictionary { get; set; } = null!;

        // ----- CAM module (Clinici de Analize Medicale) -----
        public DbSet<Clinic> Clinics { get; set; } = null!;
        public DbSet<ClinicPatient> ClinicPatients { get; set; } = null!;
        public DbSet<ClinicAnalysis> ClinicAnalyses { get; set; } = null!;
        public DbSet<ClinicBatchRun> ClinicBatchRuns { get; set; } = null!;
        public DbSet<ClinicBatchError> ClinicBatchErrors { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("Users");
                entity.HasKey(u => u.Email);
                entity.Property(u => u.Email).HasMaxLength(200);
                entity.Property(u => u.Parola).HasMaxLength(255).IsRequired();
                entity.Property(u => u.DataC).HasColumnType("datetime2");
                entity.Property(u => u.LastLoginAt).HasColumnType("datetime2");
                entity.Property(u => u.TotalPaid).HasColumnType("decimal(12,2)");
            });

            modelBuilder.Entity<InterpretationHistory>(entity =>
            {
                entity.ToTable("InterpretationHistories");
                entity.HasKey(h => h.Id);
                entity.HasIndex(h => h.UserEmail);
                entity.Property(h => h.CreatedAt).HasColumnType("datetime2");
            });

            modelBuilder.Entity<Purchase>(entity =>
            {
                entity.ToTable("Purchases");
                entity.HasKey(p => p.Id);
                entity.HasIndex(p => p.UserEmail);
                entity.HasIndex(p => p.PurchasedAt);
                entity.Property(p => p.PurchasedAt).HasColumnType("datetime2");
                entity.Property(p => p.AmountEur).HasColumnType("decimal(10,2)");
            });

            modelBuilder.Entity<PromoCode>(entity =>
            {
                entity.ToTable("PromoCodes");
                entity.HasKey(p => p.Id);
                entity.HasIndex(p => p.Code).IsUnique();
                entity.Property(p => p.Code).HasMaxLength(50);
                entity.Property(p => p.ValidFrom).HasColumnType("datetime2");
                entity.Property(p => p.ValidUntil).HasColumnType("datetime2");
                entity.Property(p => p.CreatedAt).HasColumnType("datetime2");
            });

            modelBuilder.Entity<Profile>(entity =>
            {
                entity.ToTable("Profiles");
                entity.HasKey(p => p.Id);
                entity.Property(p => p.UserEmail).HasMaxLength(200).IsRequired();
                entity.Property(p => p.Name).HasMaxLength(100).IsRequired();
                entity.Property(p => p.CreatedAt).HasColumnType("datetime2");
                entity.HasIndex(p => new { p.UserEmail, p.Name }).IsUnique();
                entity.HasIndex(p => p.UserEmail);
            });

            modelBuilder.Entity<LoincEntry>(entity =>
            {
                entity.ToTable("LoincDictionary");
                entity.HasKey(e => e.LoincCode);
                entity.Property(e => e.LoincCode).HasMaxLength(20);
                entity.Property(e => e.LongCommonName).HasMaxLength(500).IsRequired();
                entity.Property(e => e.OrderObs).HasMaxLength(20);
                // AliasesJson and TranslationsJson stay as nvarchar(max); populated later.
                entity.Property(e => e.ImportedAt).HasColumnType("datetime2");
                // Speed up future "find by common-name substring" admin searches.
                entity.HasIndex(e => e.LongCommonName);
            });

            // ===== CAM module entities =====
            modelBuilder.Entity<Clinic>(entity =>
            {
                entity.ToTable("Clinics");
                entity.HasKey(c => c.Id);
                entity.Property(c => c.UserEmail).HasMaxLength(200).IsRequired();
                entity.HasIndex(c => c.UserEmail).IsUnique();
                entity.Property(c => c.CreatedAt).HasColumnType("datetime2");
                entity.Property(c => c.FoldersCreatedAt).HasColumnType("datetime2");
            });

            modelBuilder.Entity<ClinicPatient>(entity =>
            {
                entity.ToTable("ClinicPatients");
                entity.HasKey(p => p.Id);
                entity.HasIndex(p => new { p.ClinicId, p.CnpHashKey }).IsUnique();
                entity.Property(p => p.CreatedAt).HasColumnType("datetime2");
            });

            modelBuilder.Entity<ClinicAnalysis>(entity =>
            {
                entity.ToTable("ClinicAnalyses");
                entity.HasKey(a => a.Id);
                entity.HasIndex(a => a.PatientId);
                entity.HasIndex(a => a.ClinicId);
                entity.Property(a => a.ProcessedAt).HasColumnType("datetime2");
                entity.Property(a => a.SamplingDate).HasColumnType("datetime2");
            });

            modelBuilder.Entity<ClinicBatchRun>(entity =>
            {
                entity.ToTable("ClinicBatchRuns");
                entity.HasKey(b => b.Id);
                entity.HasIndex(b => b.ClinicId);
                entity.Property(b => b.StartedAt).HasColumnType("datetime2");
                entity.Property(b => b.FinishedAt).HasColumnType("datetime2");
            });

            modelBuilder.Entity<ClinicBatchError>(entity =>
            {
                entity.ToTable("ClinicBatchErrors");
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.BatchRunId);
                entity.Property(e => e.OccurredAt).HasColumnType("datetime2");
            });
        }
    }
}
