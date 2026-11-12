
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NyxInsightsWeb.Models;

namespace NyxInsightsWeb.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext(
            DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<HealthPrediction> HealthPredictions { get; set; }

        public DbSet<ConnectedHealthAccount> ConnectedHealthAccounts { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<ConnectedHealthAccount>()
                .HasOne<IdentityUser>()
                .WithMany()
                .HasForeignKey(account => account.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ConnectedHealthAccount>()
                .HasIndex(account => account.UserId)
                .IsUnique();
        }
    }
}
