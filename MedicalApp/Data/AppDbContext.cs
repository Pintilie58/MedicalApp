using MedicalApp.Models;
using Microsoft.EntityFrameworkCore;

namespace MedicalApp.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; } = null!;
        public DbSet<InterpretationHistory> InterpretationHistories { get; set; } = null!;

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
            });

            modelBuilder.Entity<InterpretationHistory>(entity =>
            {
                entity.ToTable("InterpretationHistories");
                entity.HasKey(h => h.Id);
                entity.HasIndex(h => h.UserEmail);
                entity.Property(h => h.CreatedAt).HasColumnType("datetime2");
            });
        }
    }
}
