using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.DTOs
{
    public class ProjectResponseDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public string JsonConfiguration { get; set; } // <-- AJOUTÉ ICI pour renvoyer le brief JSON
        public DateTime CreatedAt { get; set; }
    }
}
