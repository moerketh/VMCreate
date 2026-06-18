using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace VMCreate
{
    /// <summary>
    /// Production implementation of <see cref="IDispatcher"/> wrapping the current WPF dispatcher.
    /// </summary>
    public sealed class WpfDispatcher : IDispatcher
    {
        private readonly Dispatcher _dispatcher;

        public WpfDispatcher(Dispatcher dispatcher = null)
        {
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public void Invoke(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _dispatcher.Invoke(action);
        }

        public Task InvokeAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return _dispatcher.InvokeAsync(action).Task;
        }
    }
}
