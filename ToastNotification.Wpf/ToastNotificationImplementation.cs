﻿using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Notification.FrameworkDependent;
using Notification.Natives;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.System;
using Windows.UI.Core;



namespace Uno.Extras
{
    public static class ToastNotificationImplementation
    {
        private static readonly object dummy = new object();
        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);
        private static Guid id = id = Guid.NewGuid();
        private static readonly ConfiguredTaskAwaitable<Assembly> DynamicAssemblyPromise;

        private static uint _callbackMessage = (uint)WindowMessage.App + 1;
        /// <summary>
        /// The CallbackMessage, which is an application-defined value.
        /// The value MUST be between WindowMessage.App and 0xBFFF
        /// </summary>
        public static uint CallbackMessage
        {
            get => _callbackMessage;
            set
            {
                if (Math.Min(Math.Max((uint)WindowMessage.App, value), 0xBFFFu) != value)
                {
                    throw new ArgumentOutOfRangeException("Callback message code MUST be between WindowMessage.App and 0xBFFF");
                }
            }
        }

        static ToastNotificationImplementation()
        {
            // Compiles the Assembly on a different Thread.
            DynamicAssemblyPromise = Task.Run(Compiler.Compile).ConfigureAwait(false);
        }

        /// <summary>
        /// Shows the toast notification using either Win32 APIs
        /// or a managed Popup, when neccessary
        /// </summary>
        /// <param name="toast">ToatsNotification as created in the native library.</param>
        /// <returns></returns>
        public static async Task Show(this ToastNotification toast)
        {
            await semaphore.WaitAsync();
            try
            {
				if (toast.ToastButtons == null)
				{
					await ShowLegacy(toast);
				}
				else
				{
					await ShowManaged(toast);
				}
			}
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            semaphore.Release();
        }


        /// <summary>
        /// Shows the toast notification using managed Popups.
        /// </summary>
        /// <param name="toast">ToatsNotification as created in the native library.</param>
        private static async Task ShowManaged(this ToastNotification toast)
        {
            var asm = await DynamicAssemblyPromise;

            var tcs = new TaskCompletionSource<object>();

            var loaderType = asm.GetTypes().FirstOrDefault(type => type.Name == "ToastNotificationLoader");

            dynamic loader = null;

            await RunOnMainThreadAsync(async () =>
            {
                // Python ease without Python's danger!
                loader = Activator.CreateInstance(loaderType);

                loader.Title = toast.Title;
                loader.Description = toast.Message.Trim();

                var actions = toast.ToastButtons.ToArray();

                if (actions.Length >= 1)
                {
                    loader.PrimaryButtonText = actions[0].Content;
                    loader.PrimaryButtonClick += (EventHandler)actions[0].OnButtonClick;
                }

                if (actions.Length >= 2)
                {
                    loader.SecondaryButtonText = actions[1].Content;
                    loader.SecondaryButtonClick += (EventHandler)actions[1].OnButtonClick;
                }

                if (toast.Timestamp != null)
                {
                    loader.Time = toast.Timestamp.Value.ToString("h:mm tt");
                }

                if (toast.AppLogoOverride != null)
                {
                    // We also want to stay on the same thread here.
                    var imageStream = await toast.AppLogoOverride.GetStreamAsync().ConfigureAwait(true);
                    loader.SetImageSource(imageStream);
                }

                loader.NotificationClick += (EventHandler)toast.OnToastClick;

                loader.CloseRequested += (EventHandler)HandleCloseRequest;

                Debug.WriteLine("Showing");

                await loader.ShowAsync();
                
                await Task.Delay(toast.ToastDuration == ToastDuration.Short ? 7000 : 25000);
                HandleCloseRequest(null, null);
            });

            void HandleCloseRequest(object sender, EventArgs args)
            {
                loader.HideAsync().ContinueWith((Action<Task>)(task =>
                {
                    tcs.TrySetResult(null);
                }));
            }

            await tcs.Task.ConfigureAwait(false);

            loader.CleanEvents();

        }

        /// <summary>
        /// Shows the toast notification using native Win32 APIs.
        /// </summary>
        /// <param name="toast">ToatsNotification as created in the native library.</param>
        private static async Task ShowLegacy(this ToastNotification toast)
        {
			Icon icon;
            if (toast.AppLogoOverride != null)
            {
                var imageStream = await toast.AppLogoOverride.GetStreamAsync().ConfigureAwait(false);
                var bitmap = (Bitmap)Image.FromStream(imageStream);
                icon = Icon.FromHandle(bitmap.GetHicon());
            }
            else
            {
                icon = GetProcessIcon();
            }

            var tcs = new TaskCompletionSource<object>();

            await RunOnMainThreadAsync(async () =>
            {
                var innerTcs = new TaskCompletionSource<object>();
                var notifyManager = new NotificationManagerWindow(CallbackMessage);

                var notifyData = new NotificationIconData();
                notifyData.hWnd = notifyManager.Handle;
                notifyData.cbSize = (uint)Marshal.SizeOf(notifyData);
                notifyData.uCallbackMessage = CallbackMessage;
                notifyData.DUMMYUNIONNAME_uTimeout_uVersion = 0x4;
                notifyData.hIcon = GetProcessIcon().Handle;
                notifyData.szTip = string.Empty;
                notifyData.uFlags = NotificationIconFlags.Icon | NotificationIconFlags.Tip | NotificationIconFlags.Guid | NotificationIconFlags.Message;
                notifyData.guidItem = id;

                if (!notifyData.AddIcon())
                {
                    throw new InvalidOperationException("Cannot add icon.");
                }

                notifyData.uFlags = NotificationIconFlags.Info | NotificationIconFlags.Icon | NotificationIconFlags.Guid;
                notifyData.szInfoTitle = toast.Title;
                if (string.IsNullOrEmpty(toast.Message))
                {
                    toast.Message = Environment.NewLine;
				}
				notifyData.szInfo = toast.Message;
				notifyData.dwInfoFlags = NotificationIconInfoFlags.User | NotificationIconInfoFlags.LargeIcon;
                notifyData.hBalloonIcon = icon.Handle;

                notifyManager.BalloonHide += (s, a) =>
                {
                    try
                    {
						notifyData.RemoveIcon();
						innerTcs.SetResult(null);
					}
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                };

                notifyManager.BallonClicked += (s, a) =>
                {
					try
					{
						notifyData.RemoveIcon();
						innerTcs.SetResult(null);
						_ = ActivateForeground(toast.Arguments);
					}
					catch (Exception e)
					{
						Console.WriteLine(e);
					}
                };

                Shell.NotifyIcon(NotificationIconMessage.Modify, notifyData);

                await innerTcs.Task.ConfigureAwait(true);

                // The Window must be disposed on the same Thread.
                notifyManager.Dispose();

                tcs.SetResult(null);
            });

            await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the button limit for this ToastNotification
        /// </summary>
        /// <param name="toast">The ToastNotification object</param>
        /// <returns>2</returns>
        public static int GetButtonLimit(this ToastNotification toast) => 2;

        private static bool AddIcon(this NotificationIconData notifyData)
        {
            return Shell.NotifyIcon(NotificationIconMessage.Add, notifyData) 
                && Shell.NotifyIcon(NotificationIconMessage.SetVersion, notifyData);
        }

        private static bool RemoveIcon(this NotificationIconData notifyData)
        {
            return Shell.NotifyIcon(NotificationIconMessage.Delete, notifyData);
        }

        /// <summary>
        /// Gets the duration of notifications in seconds.
        /// </summary>
        /// <returns>The duration of notification, in seconds.</returns>
        private static int GetNotificationDuration()
        {
            return Registry.CurrentUser.OpenSubKey("Control Panel")
                                      ?.OpenSubKey("Accessibility")
                                      ?.GetValue("MessageDuration") as int?
                                      ?? 9;
        }

        private static Icon GetProcessIcon()
        {
            Icon icon = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location);
            return icon;
        }

        private static async Task ActivateForeground(string argument)
        {
            var asm = await DynamicAssemblyPromise;
            var type = asm.GetTypes().FirstOrDefault(t => t.Name == "WpfHelpers");

            type.GetMethod("ActivateApp").Invoke(null, null);

            var app = Application.Current;
            var args = Reflection.Construct<ToastNotificationActivatedEventArgs>(argument);
            app.Invoke("OnActivated", new object[] { args });
        }

        private static async void OnToastClick(this ToastNotification toast, object sender, EventArgs args)
        {
            await ActivateForeground(toast.Arguments).ConfigureAwait(false);
        }

        private static async void OnButtonClick(this ToastButton button, object sender, EventArgs args)
        {
            if (button.ShouldDissmiss)
            {
                return;
            }
            switch (button.ActivationType)
            {
                case ToastActivationType.Background:
                    ToastNotification.ActivateBackground(button.Arguments);
                break;
                case ToastActivationType.Foreground:
                    await ActivateForeground(button.Arguments).ConfigureAwait(false);
                break;
                case ToastActivationType.Protocol:
                    _ = Launcher.LaunchUriAsync(button.Protocol);
                break;
                default:
                    Debug.WriteLine($"Unknown activation type: {button.ActivationType}");
                    await ActivateForeground(button.Arguments).ConfigureAwait(false);
                break;
            }
        }

        private static async Task RunOnMainThreadAsync(Action a)
        {
            await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                a();
            });
        }
    }
}
