﻿#if __ANDROID__
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using AndroidX.Core.App;
using Java.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Uno.UI;

namespace Uno.Extras
{
    public partial class ToastNotification
    {
        #region Private fields
        private const string channelId = "toast_notifications";
        private TaskCompletionSource<NotificationManager> _tcs;
        private Activity _activity;
        private Application _application;
        private NotificationManager _manager;
        private static int notificationId = 0;
        private static int intentId;

        private static int totalId = 0;
        #endregion

        internal const string NotificationAction = "com.azureams.NotificationAction";
        internal const string NotificationArgumentProperty = "argument";
        internal const string NotificationIdProperty = "android_notification_id";

        public ToastNotification()
        {
            _activity = ContextHelper.Current as Activity;
            if (_activity == null)
            {
                throw new InvalidOperationException("Cannot send ToastNotifications without a valid Windows.UI.Xaml.ApplicationActivity.");
            }
            _application = (Application)_activity.ApplicationContext;

            notificationId = Interlocked.Increment(ref totalId);
        }

        /// <summary>
        /// Shows the toast notification.
        /// </summary>
        public async Task Show()
        {
            if (_manager == null)
            {
                _manager = await GetNotificationManager();
                // Something bad happend.
                if (_manager == null)
                {
                    throw new InvalidOperationException("Failed to get toast notification manager for unknown reasons.");
                }
                CreateNotificationChannel();
            }

            var iconId = _application.ApplicationInfo.Icon;
            if (iconId == 0)
            {
                iconId = Android.Resource.Drawable.SymDefAppIcon;
            }

            var notificationIntent = new Intent(_activity, typeof(ToastNotificationHandler));

            notificationIntent.SetAction(NotificationAction);
            notificationIntent.PutExtra(NotificationArgumentProperty, "foreground," + Arguments);
            notificationIntent.PutExtra(NotificationIdProperty, notificationId);
            notificationIntent.SetAction(Intent.ActionMain);

            var pendingIntent = PendingIntent.GetService(_activity, intentId++, notificationIntent, PendingIntentFlags.UpdateCurrent);

            NotificationCompat.Builder builder = new NotificationCompat.Builder(_activity, channelId)
                .SetContentTitle(Title)
                .SetContentText(Message)
                .SetSmallIcon(iconId)
                .SetPriority((int)NotificationPriority.Max)
                .SetVibrate(new[] { 0L })
                .SetContentIntent(pendingIntent);

            if (ToastButtons != null)
            {
                foreach (var button in ToastButtons)
                {
                    var buttonIntent = new Intent(_activity, typeof(ToastNotificationHandler));
                    buttonIntent.SetAction(NotificationAction);
                    buttonIntent.PutExtra(NotificationArgumentProperty, GetAppropriateArgument(button));
                    buttonIntent.PutExtra(NotificationIdProperty, notificationId);
                    var buttonPendingIntent = PendingIntent.GetService(_activity, intentId++, buttonIntent, PendingIntentFlags.UpdateCurrent);
                    builder.AddAction(0, button.Content, buttonPendingIntent);
                }
            }

            if (AppLogoOverride != null)
            {
                var stream = await AppLogoOverride.GetStreamAsync().ConfigureAwait(false);
                var bitmap = await BitmapFactory.DecodeStreamAsync(stream).ConfigureAwait(false);
                builder.SetLargeIcon(bitmap);
            }

            if (Timestamp != null)
            {
                builder.SetShowWhen(true);
                builder.SetWhen(new DateTimeOffset(Timestamp.Value).ToUnixTimeMilliseconds());
            }

            var notification = builder.Build();

            _manager.Notify(notificationId, notification);
        }


        // Accoring to https://developer.android.com/reference/android/app/Notification.Builder.html#addAction(android.app.Notification.Action)
        public int GetButtonLimit() => 3;

        private Task<NotificationManager> GetNotificationManager()
        {
            _tcs = new TaskCompletionSource<NotificationManager>();

            try
            {
                if (_activity.GetSystemService(Context.NotificationService) is NotificationManager result)
                {
                    _tcs.SetResult(result);
                }
                return _tcs.Task;
            }
            catch (Java.Lang.Exception)
            {
                // Silence it here, retry.
            }

            _activity.RegisterActivityLifecycleCallbacks(new Callbacks(this));

            try
            {
                if (_activity.GetSystemService(Context.NotificationService) is NotificationManager result)
                {
                    _tcs.SetResult(result);
                }
            }
            catch (Java.Lang.Exception)
            {
                // Silence it here.
            }

            return _tcs.Task;
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            {
                // Notification channels are new in API 26 (and not a part of the
                // support library). There is no need to create a notification
                // channel on older versions of Android.
                return;
            }

            var channelName = "General notifications";
            var channelDescription = "Toast notifications from application.";
            var channel = new NotificationChannel(channelId, channelName, NotificationImportance.Default)
            {
                Description = channelDescription
            };

            _manager.CreateNotificationChannel(channel);
        }

        #region IActivityLifecycleCallbacks
        private class Callbacks : Java.Lang.Object, Application.IActivityLifecycleCallbacks
        {
            private ToastNotification _owner;

            public Callbacks(ToastNotification owner)
            {
                _owner = owner;
            }

            public void OnActivityCreated(Activity activity, Bundle savedInstanceState)
            {
                // Task completion source might be set before, on the second attempt.
                _owner?._tcs?.TrySetResult(activity.GetSystemService(Context.NotificationService) as NotificationManager);
                _owner = null;
            }

            public void OnActivityDestroyed(Activity activity)
            {
                // Not interested
            }

            public void OnActivityPaused(Activity activity)
            {
                // Not interested
            }

            public void OnActivityResumed(Activity activity)
            {
                // Not interested
            }

            public void OnActivitySaveInstanceState(Activity activity, Bundle outState)
            {
                // Not interested
            }

            public void OnActivityStarted(Activity activity)
            {
                // Not interested
            }

            public void OnActivityStopped(Activity activity)
            {
                // Not interested
            }
        }

        #endregion
    }
}
#endif