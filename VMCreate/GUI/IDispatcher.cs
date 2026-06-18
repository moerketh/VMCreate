using System;
using System.Threading.Tasks;

namespace VMCreate
{
    /// <summary>
    /// Thin abstraction over a UI dispatcher so presenters can be unit-tested
    /// without a live WPF runtime.
    /// </summary>
    public interface IDispatcher
    {
        void Invoke(Action action);
        Task InvokeAsync(Action action);
    }
}
