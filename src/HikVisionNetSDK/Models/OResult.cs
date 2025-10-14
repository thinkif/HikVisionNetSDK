using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


[Serializable]
public readonly struct OResult<T> : IEquatable<OResult<T>>
{
    [Description("成功")]
    public const int CODE_SUCCESS = 0;

    [Description("失败")]
    public const int CODE_FAILURE = 1;

    public static OResult<T> Default { get; }

    public Exception Exception { get; }

    public bool Success { get; }

    public T Value { get; }

    public int ErrorCode { get; }

    public string Message { get; }

    static OResult()
    {
        Default = new OResult<T>(default(T));
    }

    public OResult(T value)
        : this(value, 0)
    {
    }

    public OResult(T value, Exception exception)
        : this(value, 1, exception)
    {
    }

    public OResult(T value, string errorMessage)
        : this(value, 1, errorMessage)
    {
    }

    public OResult(T value, int errorCode)
    {
        Value = value;
        ErrorCode = errorCode;
        Message = null;
        Exception = null;
        Success = true;
    }

    public OResult(T value, int errorCode, Exception exception)
    {
        Value = value;
        ErrorCode = errorCode;
        Message = exception?.Message;
        Exception = exception;
        Success = false;
    }

    public OResult(T value, int errorCode, string errorMessage)
    {
        Value = value;
        ErrorCode = errorCode;
        Message = errorMessage;
        Exception = null;
        Success = false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Value, ErrorCode, Exception) & 0x7FFFFFFF;
    }

    public bool Equals(OResult<T> other)
    {
        return GetHashCode() == other.GetHashCode();
    }
}
