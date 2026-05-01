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
        }
    }
}
