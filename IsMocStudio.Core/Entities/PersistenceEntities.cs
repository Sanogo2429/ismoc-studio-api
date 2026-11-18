namespace IsMocStudio.Core.Entities
{
    /// <summary>
    /// Discussion / projet IsMoc (anciennement App_Data/Memories/{projectId}.json) : le ProjectMemoryDto complet
    /// est stocké en jsonb dans <see cref="Document"/> ; les autres colonnes servent aux recherches et aux tris.
    /// </summary>
    public class ProjectMemoryRecord
    {
        public string ProjectId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string ThreadType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string Document { get; set; } = "{}";
    }

    /// <summary>Dossier de conception livré à l'équipe (anciennement App_Data/Deliverables/{projectId}/).</summary>
    public class ProjectDeliverableRecord
    {
        public string ProjectId { get; set; } = string.Empty;
        public string DesignHtml { get; set; } = string.Empty;

        /// <summary>cahier-des-charges.json tel quel (texte : l'ordre des clés et la mise en forme sont conservés).</summary>
        public string SpecificationJson { get; set; } = "{}";
        public DateTimeOffset GeneratedAt { get; set; }
    }

    /// <summary>
    /// Pièce jointe ou note vocale d'un projet (le contenu est dans le stockage de fichiers, clé <see cref="StorageKey"/>).
    /// </summary>
    public class ProjectAttachmentRecord
    {
        /// <summary>Nom public « {guid}__{nom-original} », utilisé dans l'URL de l'API.</summary>
        public string FileName { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>Clé ASCII dans le stockage : projects/{projectId}/{guid}{extension}.</summary>
        public string StorageKey { get; set; } = string.Empty;
        public string OriginalName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long Size { get; set; }

        /// <summary>« file » ou « voice ».</summary>
        public string Kind { get; set; } = "file";
        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>Crédits IA gratuits consommés par un compte (anciennement App_Data/ai-quotas.json).</summary>
    public class AiQuotaUsage
    {
        public string UserId { get; set; } = string.Empty;
        public int GenerationsUsed { get; set; }
        public int RefinementsUsed { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
