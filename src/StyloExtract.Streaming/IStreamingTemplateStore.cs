namespace StyloExtract.Streaming;

public interface IStreamingTemplateStore
{
    StreamingTemplate? Get(Guid templateId);
    void Register(StreamingTemplate template);
}
