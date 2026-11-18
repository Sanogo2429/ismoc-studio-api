using IsMocStudio.Core.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System;

namespace IsMocStudio.Data.Context
{
    public class IsMocDbContext : DbContext, IDataProtectionKeyContext
    {
        public IsMocDbContext(DbContextOptions<IsMocDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Project> Projects { get; set; }

        // Données qui étaient dans App_Data (disque effacé à chaque redéploiement Render).
        public DbSet<ProjectMemoryRecord> ProjectMemories => Set<ProjectMemoryRecord>();
        public DbSet<ProjectDeliverableRecord> ProjectDeliverables => Set<ProjectDeliverableRecord>();
        public DbSet<AiQuotaUsage> AiQuotaUsages => Set<AiQuotaUsage>();

        // Clés ASP.NET Core DataProtection (cookies de la connexion Google) : conservées entre les redéploiements.
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

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

            // ==========================================
            // DISCUSSIONS / PROJETS (ex-App_Data/Memories) : document jsonb + colonnes de recherche
            // ==========================================
            modelBuilder.Entity<ProjectMemoryRecord>(entity =>
            {
                entity.ToTable("ProjectMemories");
                entity.HasKey(memory => memory.ProjectId);
                entity.Property(memory => memory.ProjectId).HasMaxLength(32);
                entity.Property(memory => memory.OwnerId).IsRequired().HasMaxLength(64);
                entity.Property(memory => memory.ThreadType).IsRequired().HasMaxLength(20);
                entity.Property(memory => memory.Status).IsRequired().HasMaxLength(30);
                entity.Property(memory => memory.ProjectName).IsRequired().HasMaxLength(200);
                entity.Property(memory => memory.Document).IsRequired().HasColumnType("jsonb");
                entity.HasIndex(memory => memory.OwnerId);
                entity.HasIndex(memory => memory.UpdatedAt);
            });

            // ==========================================
            // DOSSIERS DE CONCEPTION (ex-App_Data/Deliverables) : textes conservés tels quels
            // ==========================================
            modelBuilder.Entity<ProjectDeliverableRecord>(entity =>
            {
                entity.ToTable("ProjectDeliverables");
                entity.HasKey(deliverable => deliverable.ProjectId);
                entity.Property(deliverable => deliverable.ProjectId).HasMaxLength(32);
                entity.Property(deliverable => deliverable.DesignHtml).IsRequired().HasColumnType("text");
                entity.Property(deliverable => deliverable.SpecificationJson).IsRequired().HasColumnType("text");
            });

            // ==========================================
            // CRÉDITS IA GRATUITS (ex-App_Data/ai-quotas.json)
            // ==========================================
            modelBuilder.Entity<AiQuotaUsage>(entity =>
            {
                entity.ToTable("AiQuotaUsages");
                entity.HasKey(usage => usage.UserId);
                entity.Property(usage => usage.UserId).HasMaxLength(64);
            });
        }
    }
}