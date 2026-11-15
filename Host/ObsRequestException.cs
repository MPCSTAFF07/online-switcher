public sealed class ObsRequestException : Exception
{
    public string RequestType { get; }
    public int Code { get; }

    public ObsRequestException(string requestType, int code, string? comment)
        : base($"OBS {requestType} 失敗（{code}）: {Explain(code, comment)}")
    {
        RequestType = requestType;
        Code = code;
    }

    static string Explain(int code, string? comment)
    {
        if (!string.IsNullOrWhiteSpace(comment)) return comment;
        return code switch
        {
            506 => "OBSのスタジオモードがOFFです。OBS右下の「スタジオモード」をONにして再試行してください。",
            505 => "OBSのスタジオモードは既にONです。",
            _ => "OBSから追加の説明は返されませんでした。"
        };
    }
}
