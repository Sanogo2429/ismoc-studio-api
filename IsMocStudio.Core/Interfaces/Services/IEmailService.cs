using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.Interfaces.Services
{
    // Cette méthode promet qu'on pourra envoyer un e-mail 
    // avec un destinataire (to), un sujet (subject) et un message (body).
    public interface IEmailService
    {
        Task SendEmailAsync(string to, string subject, string body);
    }
}
