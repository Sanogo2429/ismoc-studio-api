using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.DTOs
{
    public class CreateProjectDto
    {
        public string Name { get; set; }
        public string Type { get; set; } // "Web" ou "Mobile"
        public string Description { get; set; } // Optionnel pour l'instant
    }
}
