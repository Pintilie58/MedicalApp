namespace MedicalApp.Services
{
    public class OpenAISettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = "gpt-4o-mini";
        public int MaxOutputTokens { get; set; } = 4000;
        public float Temperature { get; set; } = 0.3f;
        public int TimeoutSeconds { get; set; } = 60;
    }
}
