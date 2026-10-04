namespace NetLoop.Libzt;

public sealed class LibztException : IOException
{
    public LibztException(string operation, int apiResult, int socketError = 0)
        : base($"{operation} failed: api_rc={apiResult}, socket_error={socketError}")
    {
        Operation = operation;
        ApiResult = apiResult;
        SocketError = socketError;
    }

    public string Operation { get; }

    public int ApiResult { get; }

    public int SocketError { get; }
}
