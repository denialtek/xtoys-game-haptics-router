using IntifaceGameHapticsRouter;
using SharpMonoInjector;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EasyHook;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.ComponentModel;
using NLog;
using System.IO;
using Newtonsoft.Json;

namespace XToysGameHaptics
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private const string XTOYS_VERSION = @"2.0";
        private bool _attached = false;
        private Logger _log;
        private ProcessInfoList _processList = new ProcessInfoList();
        private Task _enumProcessTask;
        private Task _xtoysReadTask;
        private UnityVRMod _unityMod;
        private XInputMod _xinputMod;
        private CancellationTokenSource _scanningTokenSource = null;
        private CancellationToken _scanningToken;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            _log = LogManager.GetCurrentClassLogger();

            WriteToXToys(new XToysStatusMessage("version", XTOYS_VERSION));
            _xtoysReadTask = new Task(() => ReadFromXToys());
            _xtoysReadTask.Start();

            refreshProcessList();
        }

        class XToysBaseMessage { }
        class XToysReceivedMessage : XToysBaseMessage
        {
            public bool? passthru;
            public bool? refresh;
            public int? connect;
        }
        class XToysStatusMessage : XToysBaseMessage
        {
            public string eventName;
            public string data;
            public XToysStatusMessage(string eventName)
            {
                this.eventName = eventName;
            }
            public XToysStatusMessage(string eventName, string data)
            {
                this.eventName = eventName;
                this.data = data;
            }
        }
        class XToysVibrationMessage : XToysBaseMessage
        {
            public string eventName = "vibrate";
            public uint left;
            public uint right;
            public XToysVibrationMessage(uint left, uint right)
            {
                this.left = left;
                this.right = right;
            }
        }
        class XToysProcessMessage : XToysBaseMessage
        {
            public string eventName = "processList";
            public Dictionary<string, object>[] processes;
            public XToysProcessMessage(ProcessInfoList processList)
            {
                var simplifiedProcessList = new List<Dictionary<string, object>>();
                foreach (ProcessInfo process in processList) {
                    Dictionary<string, object> basicProcessInfo = new Dictionary<string, object>();
                    basicProcessInfo.Add("name", process.FileName);
                    basicProcessInfo.Add("id", process.Id);
                    simplifiedProcessList.Add(basicProcessInfo);
                }
                processes = simplifiedProcessList.ToArray();
            }
        }
        public void ReadFromXToys()
        {
            while (true) // continue listening to stdin until Chrome kills the connection
            {
                var stdin = Console.OpenStandardInput();
                var lengthBytes = new byte[4];
                var result = stdin.Read(lengthBytes, 0, 4);

                _log.Debug(result);

                if (result == 0)
                {
                    // Result 0 means the Chrome extension closed the session (or the user is trying to directly run the app instead of through the XToys website).
                    // If so close the app.
                    _log.Info("Exiting due to stdin read of 0");
                    Dispatcher.Invoke(() =>
                    {
                        Detach();
                        Application.Current.Shutdown();
                    });
                }
                else
                {
                    // XToys website is sending a message. Parse and act on it.
                    var length = BitConverter.ToInt32(lengthBytes, 0);
                    var buffer = new char[length];
                    using (var reader = new StreamReader(stdin))
                    {
                        while (reader.Peek() >= 0)
                        {
                            reader.Read(buffer, 0, buffer.Length);
                        }
                    }
                    var xtoysMessage = new string(buffer);
                    XToysReceivedMessage parsedMessage = JsonConvert.DeserializeObject<XToysReceivedMessage>(xtoysMessage);
                    if (parsedMessage.passthru.HasValue)
                    {
                        GHRXInputModInterface.GHRXInputModInterface._shouldPassthru = parsedMessage.passthru.Value;
                    }
                    if (parsedMessage.refresh.HasValue)
                    {
                        refreshProcessList();
                    }
                    if (parsedMessage.connect.HasValue)
                    {
                        var gameID = parsedMessage.connect.Value;
                        foreach (ProcessInfo process in _processList)
                        {
                            if (process.Id == gameID)
                            {
                                Dispatcher.Invoke(() => { attachToProcess(process); });
                            }
                        }
                    }
                }
            }
        }

        private void WriteToXToys(XToysBaseMessage data)
        {
            string stringData = JsonConvert.SerializeObject(data);
            //// We need to send the 4 btyes of length information
            int DataLength = stringData.Length;
            Stream stdout = Console.OpenStandardOutput();
            stdout.WriteByte((byte)((DataLength >> 0) & 0xFF));
            stdout.WriteByte((byte)((DataLength >> 8) & 0xFF));
            stdout.WriteByte((byte)((DataLength >> 16) & 0xFF));
            stdout.WriteByte((byte)((DataLength >> 24) & 0xFF));
            //Available total length : 4,294,967,295 ( FF FF FF FF )
            Console.Write(stringData);
            _log.Debug(stringData);
        }

        public class ProcessInfo
        {
            public string FileName;
            public int Id;
            public string Owner;
            public IntPtr MonoModule = IntPtr.Zero;
            public UnityVRMod.NetFramework FrameworkVersion = UnityVRMod.NetFramework.UNKNOWN;

            public bool CanUseXInput => !string.IsNullOrEmpty(Owner);

            public bool CanUseMono => MonoModule != IntPtr.Zero;

            public override string ToString()
            {
                var f = System.IO.Path.GetFileNameWithoutExtension(FileName);
                return $"{f} ({Id}) ({(CanUseMono ? $"Mono/{FrameworkVersion}" : "")}{(CanUseXInput && CanUseMono ? " | " : "")}{(CanUseXInput ? "XInput" : "")})";
            }

            public bool IsLive => Process.GetProcessById(Id) != null;
        }

        private class ProcessInfoList : ObservableCollection<ProcessInfo>
        {
        }


        private void refreshProcessList()
        {
            WriteToXToys(new XToysStatusMessage("scanning"));
            if (_scanningTokenSource != null)
            {
                _scanningTokenSource.Cancel();
                _enumProcessTask.Wait();
            }
            _scanningTokenSource = new CancellationTokenSource();
            _scanningToken = _scanningTokenSource.Token;
            _enumProcessTask = new Task(() => EnumProcesses());
            _enumProcessTask.Start();
        }

        private void EnumProcesses()
        {
            Dispatcher.Invoke(() => { _processList.Clear(); }); // probably doesn't need dispatch
            var cp = Process.GetCurrentProcess().Id;
            const ProcessAccessRights flags = ProcessAccessRights.PROCESS_QUERY_INFORMATION | ProcessAccessRights.PROCESS_VM_READ;
            var procList = from proc in Process.GetProcesses() orderby proc.ProcessName select proc;
            Parallel.ForEach(procList, (currentProc) =>
            {
                if (_scanningToken.IsCancellationRequested)
                {
                    return;
                }
                var handle = IntPtr.Zero;

                try
                {
                    // This can sometimes happen between calling GetProcesses and getting here. Save ourselves the throw.
                    if (currentProc.HasExited || currentProc.Id == cp)
                    {
                        return;
                    }

                    // This is usually what throws, so do it before we invoke via dispatcher.
                    var owner = RemoteHooking.GetProcessIdentity(currentProc.Id).Name;

                    if ((handle = Native.OpenProcess(flags, false, currentProc.Id)) == IntPtr.Zero)
                    {
                        return;
                    }

                    var procInfo = new ProcessInfo
                    {
                        FileName = currentProc.ProcessName,
                        Id = currentProc.Id,
                    };

                    if (XInputMod.CanUseMod(handle))
                    {
                        procInfo.Owner = owner;
                    }

                    if (UnityVRMod.CanUseMod(handle, currentProc.MainModule.FileName, out var module, out var frameworkVersion))
                    {
                        procInfo.MonoModule = module;
                        procInfo.FrameworkVersion = frameworkVersion;
                    }

                    if (procInfo.CanUseXInput || procInfo.CanUseMono)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _log.Debug(procInfo);
                            _processList.Add(procInfo);
                        });
                    }
                }
                catch (AccessViolationException)
                {
                    // noop, there's a lot of system processes we can't see.
                }
                catch (Win32Exception)
                {
                    // noop, there's a lot of system processes we can't see.
                }
                catch (SharpMonoInjector.InjectorException)
                {
                    //
                }
                catch (Exception aEx)
                {
                    _log.Error(aEx);
                }
                finally
                {
                    // Only close the 
                    if (handle != IntPtr.Zero)
                    {
                        Native.CloseHandle(handle);
                    }
                }
            });
            if (!_attached)
            {
                WriteToXToys(new XToysProcessMessage(_processList));
            }
            _scanningTokenSource = null;
            _enumProcessTask = null;
        }
        public bool Attached
        {
            get
            {
                return _attached;
            }
            set
            {
                _attached = value;
                // Notify attached
            }
        }

        private void attachToProcess(ProcessInfo process)
        {
            Confirm confirmWindow = new Confirm
            {
                ConfirmationText = { Text = "XToys would like to connect to " + process.FileName + ". Continue?" }
            };
            confirmWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            confirmWindow.Topmost = true;
            var confirmResult = confirmWindow.ShowDialog();
            if (confirmResult != true)
            {
                Application.Current.Shutdown(); // user rejected connection. Kill app.
                return;
            }

            if (!Attached)
            {
                if (_scanningTokenSource != null && _scanningToken.CanBeCanceled)
                {
                    _scanningTokenSource.Cancel();
                }

                if (!process.IsLive)
                {
                    return;
                }

                var attached = false;
                try
                {
                    if (process.CanUseMono)
                    {
                        _unityMod = new UnityVRMod();
                        _unityMod.MessageReceivedHandler += OnMessageReceived;
                        _unityMod.Inject(process.Id, process.FrameworkVersion, process.MonoModule);
                        attached = true;
                    }

                    if (process.CanUseXInput)
                    {
                        _xinputMod = new XInputMod();
                        _xinputMod.Attach(process.Id);
                        _xinputMod.MessageReceivedHandler += OnMessageReceived;
                        attached = true;
                    }

                    if (attached)
                    {
                        Attached = true;
                        WriteToXToys(new XToysStatusMessage("connected", process.FileName));
                    }
                }
                catch
                {
                    Attached = false;
                }
            }
            else
            {
                Detach();
            }
        }
        private void OnMessageReceived(object aObj, GHRProtocolMessageContainer aMsg)
        {
            if (aMsg.UnityXRViveHaptics != null || aMsg.UnityXROculusClipHaptics != null || aMsg.UnityXROculusInputHaptics != null)
            {
                WriteToXToys(new XToysVibrationMessage(65535, 65535));
            }
            else if (aMsg.XInputHaptics != null)
            {
                WriteToXToys(new XToysVibrationMessage(aMsg.XInputHaptics.LeftMotor, aMsg.XInputHaptics.RightMotor));
                Debug.WriteLine(JsonConvert.SerializeObject(aMsg.XInputHaptics));
            }
            else if (aMsg.Log != null)
            {
                _log.Info(aMsg.Log.Message);
                Debug.WriteLine(aMsg.Log.Message);
            }
        }

        private void Detach()
        {
            _xinputMod?.Detach();
            _xinputMod = null;
            Attached = false;
            WriteToXToys(new XToysStatusMessage("disconnected"));
        }
    }
}
