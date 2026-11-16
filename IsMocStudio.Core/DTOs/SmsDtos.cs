namespace IsMocStudio.Core.DTOs
{
    public class SendSmsRequestDto
    {
        public string PhoneNumber { get; set; } = string.Empty;
    }

    public class VerifySmsRequestDto
    {
        public string PhoneNumber { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }
}