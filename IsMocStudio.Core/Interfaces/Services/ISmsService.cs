using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.Interfaces.Services
{
    public interface ISmsService
    {
        // Une seule mission : envoyer un SMS à un numéro avec un message
        Task SendSmsAsync(string phoneNumber, string message);
    }
}
