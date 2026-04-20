namespace MedicalApp.Services
{
    public class OpenAISettings
    {
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = "gpt-4o-mini";
        public int MaxOutputTokens { get; set; } = 8000;
        public float Temperature { get; set; } = 0.0f;
        public int TimeoutSeconds { get; set; } = 120;
        public long Seed { get; set; } = 42;
    }
}
