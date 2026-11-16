using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.Interfaces.Services
{
    public interface IAiService
    {
        Task<string> GenerateJsonBriefAsync(string projectType, string description);
    }
    
}
