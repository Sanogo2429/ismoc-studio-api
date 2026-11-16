using IsMocStudio.Core.Entities;
using Microsoft.EntityFrameworkCore;
using System;

namespace IsMocStudio.Data.Context
{
    public class IsMocDbContext : DbContext
    {
        public IsMocDbContext(DbContextOptions<IsMocDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Project> Projects { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ==========================================
            // CONFIGURATION DE L'ENTITÉ USER
            // ==========================================
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(u => u.Id);
                entity.HasIndex(u => u.Email).IsUnique();

                entity.Property(u => u.Email)
                    .IsRequired()
                    .HasMaxLength(150);

                entity.Property(u => u.FirstName)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.Property(u => u.LastName)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.Property(u => u.PhoneNumber)
                    .HasMaxLength(20);

                entity.Property(u => u.PasswordHash)
                    .IsRequired();

                entity.Property(u => u.Role)
                    .IsRequired()
                    .HasMaxLength(30)
                    .HasDefaultValue("Client");
            });

            // ==========================================
            // CONFIGURATION DE L'ENTITÉ PROJECT (Epic 2)
            // ==========================================
            modelBuilder.Entity<Project>(entity =>
            {
                entity.HasKey(p => p.Id);

                entity.Property(p => p.Name)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(p => p.Type)
                    .IsRequired()
                    .HasMaxLength(50); // Ex: "Web" ou "Mobile"

                entity.Property(p => p.Description)
                    .HasMaxLength(2000); // Pour stocker le prompt ou le besoin détaillé de l'IA (US 2.2)

                entity.Property(p => p.JsonConfiguration)
                    .HasColumnType("text"); // Texte de longueur illimitée (PostgreSQL) pour le gros JSON de configuration/modules (US 2.2 & 2.3)

                entity.Property(p => p.CreatedAt)
                    .IsRequired()
                    .HasDefaultValueSql("now()"); // Date automatique en base (timestamp with time zone, stocké en UTC)

                // Relation 1-à-plusieurs : Un utilisateur possède plusieurs projets
                entity.HasOne(p => p.User)
                    .WithMany()
                    .HasForeignKey(p => p.UserId)
                    .OnDelete(DeleteBehavior.Cascade); // Si on supprime l'utilisateur, ses projets sont nettoyés
            });
        }
    }
}