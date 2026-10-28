using System;
using System.Diagnostics;

namespace UnityAIBridge
{
    internal sealed class RunActivitySnapshot
    {
        public string Phase;
        public string Detail;
        public string Health;
        public double ElapsedSeconds;
        public double SecondsSinceProgress;
        public double SecondsSinceData;
        public bool MayBeStalled;
        public bool AwaitingReview;
    }

    // Uses observed events and a monotonic clock. A local animation is never evidence
    // that the provider is alive: transport traffic and model progress have separate ages.
    internal sealed class RunActivityTracker
    {
        readonly object _gate = new object();
        readonly Func<double> _now;
        double _started, _progress, _data;
        string _phase = "Waiting for provider", _detail = "";
        bool _hasData, _waiting = true;

        public RunActivityTracker(Func<double> clock = null)
        {
            _now = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        }

        public void Begin()
        {
            lock (_gate)
            {
                _started = _progress = _now();
                _data = 0;
                _hasData = false;
                _phase = "Waiting for provider";
                _detail = "";
                _waiting = true;
            }
        }

        public void Observe(StreamEvent evt)
        {
            if (evt == null) return;
            lock (_gate)
            {
                double now = _now();
                if (evt.Kind == StreamEventKind.TransportActivity)
                {
                    _hasData = true;
                    _data = now;
                    return;
                }
                if (evt.Kind == StreamEventKind.Usage) return;
                if ((evt.Kind == StreamEventKind.TextDelta || evt.Kind == StreamEventKind.ReasoningDelta) && string.IsNullOrEmpty(evt.Text)) return;
                _progress = now;
                _detail = "";
                _waiting = false;
                switch (evt.Kind)
                {
                    case StreamEventKind.TextDelta: _phase = "Writing response"; break;
                    case StreamEventKind.ReasoningDelta: _phase = "Thinking"; break;
                    case StreamEventKind.ToolCallStarted:
                    case StreamEventKind.ToolCallArguments:
                        _phase = "Preparing tool"; _detail = evt.ToolCall != null ? evt.ToolCall.Name : ""; break;
                    case StreamEventKind.ToolExecutionStarted:
                        _detail = evt.ToolCall != null ? evt.ToolCall.Name : "";
                        _phase = IsReadTool(_detail) ? "Reading project" : IsEditTool(_detail) ? "Editing project" : "Running tool";
                        break;
                    case StreamEventKind.Completed: _phase = "Finalizing"; break;
                    case StreamEventKind.Error: _phase = "Provider error"; break;
                    default:
                        _phase = "Waiting for provider";
                        _waiting = true;
                        break;
                }
                if (evt.Kind == StreamEventKind.TextDelta || evt.Kind == StreamEventKind.ReasoningDelta ||
                    evt.Kind == StreamEventKind.ToolCallStarted || evt.Kind == StreamEventKind.ToolCallArguments)
                {
                    _hasData = true;
                    _data = now;
                    _waiting = true;
                }
            }
        }

        public void Stopping()
        {
            lock (_gate) { _phase = "Stopping"; _detail = ""; _waiting = false; _progress = _now(); }
        }

        public RunActivitySnapshot Snapshot(bool awaitingReview = false)
        {
            lock (_gate)
            {
                double now = _now(), age = Math.Max(0, now - (_hasData ? _data : _started));
                double quiet = Math.Max(0, now - _progress);
                bool stalled = _waiting && !awaitingReview && age >= 45 && quiet >= 45;
                string health = awaitingReview ? "Accept, deny or comment to continue"
                    : !_waiting ? "Last activity " + (int)quiet + "s ago"
                    : !_hasData ? "No provider data yet · waiting " + (int)age + "s"
                    : age < 15 ? "Provider connected · last data " + (int)age + "s ago"
                    : "No provider data for " + (int)age + "s";
                if (stalled) health += " · provider may be stalled; Stop is available";
                if (_waiting && _hasData && !awaitingReview) health += " · last progress " + (int)quiet + "s ago";
                return new RunActivitySnapshot
                {
                    Phase = awaitingReview ? "Waiting for your approval" : _phase,
                    Detail = awaitingReview ? "" : _detail,
                    Health = health,
                    ElapsedSeconds = Math.Max(0, now - _started),
                    SecondsSinceProgress = quiet,
                    SecondsSinceData = _hasData ? age : -1,
                    MayBeStalled = stalled,
                    AwaitingReview = awaitingReview
                };
            }
        }

        static bool IsReadTool(string name)
        {
            return name.StartsWith("read_", StringComparison.Ordinal) || name.StartsWith("inspect_", StringComparison.Ordinal) ||
                name.StartsWith("list_", StringComparison.Ordinal) || name.StartsWith("get_", StringComparison.Ordinal) || name == "project_summary";
        }

        static bool IsEditTool(string name)
        {
            return name.StartsWith("create_", StringComparison.Ordinal) || name.StartsWith("propose_", StringComparison.Ordinal) ||
                name.StartsWith("update_", StringComparison.Ordinal) || name.StartsWith("apply_", StringComparison.Ordinal) ||
                name.StartsWith("import_", StringComparison.Ordinal) || name.StartsWith("assign_", StringComparison.Ordinal) ||
                name.StartsWith("set_", StringComparison.Ordinal) || name.StartsWith("slice_", StringComparison.Ordinal) || name == "save_scene";
        }
    }
}
