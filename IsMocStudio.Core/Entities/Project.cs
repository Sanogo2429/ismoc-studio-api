using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.Entities
{
    public class Project
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } // "Web" ou "Mobile"
        public string Description { get; set; } // Pour l'IA (US 2.2)
        public string JsonConfiguration { get; set; } // Stockera le brief JSON généré ou personnalisé
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Clé étrangère vers l'utilisateur (Client)
        public int UserId { get; set; }
        public User User { get; set; }
    }
}
