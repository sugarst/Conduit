namespace HandlerR.Contracts;

/// <summary>
/// 流式请求的形状标记，响应以 <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/> 逐项产出。
/// </summary>
/// <typeparam name="TResponse">单项元素类型。</typeparam>
public interface IStreamRequest<out TResponse>
{
}
