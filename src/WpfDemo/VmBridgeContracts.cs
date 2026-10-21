#nullable disable
namespace AxisLab.WpfDemo.VisionBridge
{
    // Shared wire DTOs. The WPF host and VM worker use different CLR versions.
    public sealed class BridgeRequest
    {
        public string Id { get; set; }
        public string Command { get; set; }
        public string SolutionPath { get; set; }
        public string Password { get; set; }
        public string Flow { get; set; }
        public string Source { get; set; }
        public bool Mono { get; set; }
        public string InputFile { get; set; }
        public string InputSha { get; set; }
        public string FrameId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string MappingJson { get; set; }
    }
    public sealed class BridgeReply
    {
        public string Id { get; set; }
        public bool Success { get; set; }
        public string Payload { get; set; }
        public string Error { get; set; }
    }
}
