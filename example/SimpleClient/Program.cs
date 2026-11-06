using System;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using SimpleClient;

namespace SimpleClientTerminalGui
{
    public sealed class TerminalGuiUI
    {
        // UI элементы для дашборда
        public Label StatusLabel { get; private set; } = null!;
        public Label TimeLabel { get; private set; } = null!;
        public Label ActivityLabel { get; private set; } = null!;
        public FrameView DashboardFrame { get; private set; } = null!;
        public FrameView MenuFrame { get; private set; } = null!;

        // Terminal.Gui v2: інстанс застосунку замість статичного Application.MainLoop.
        private readonly IApplication _app;

        public TerminalGuiUI(IApplication app)
        {
            _app = app;
        }

        public void InitializeUI()
        {
            // Создаем рамку для дашборда с фиксированной высотой
            DashboardFrame = new FrameView
            {
                Title = "Система мониторинга",
                X = 0,
                Y = 0,
                Width = Dim.Percent(100),
                Height = 6
            };
            StatusLabel = new Label
            {
                Text = "Статус: N/A",
                X = 1,
                Y = 1,
            };
            TimeLabel = new Label
            {
                Text = "Время: 0000-00-00 00:00:00",
                X = 1,
                Y = 2,
            };
            ActivityLabel = new Label
            {
                Text = "Последняя активность: Нет активности",
                X = 1,
                Y = 3,
            };
            DashboardFrame.Add(StatusLabel, TimeLabel, ActivityLabel);

            // Создаем рамку для меню, которая занимает оставшуюся часть экрана
            MenuFrame = new FrameView
            {
                Title = "Меню",
                X = 0,
                Y = Pos.Bottom(DashboardFrame),
                Width = Dim.Percent(100),
                Height = Dim.Fill()
            };
        }

        // Обновление статуса системы (вызывается из обработчика событий)
        public void UpdateSystemStatus(string status, DateTime timestamp)
        {
            _app.Invoke(() =>
            {
                StatusLabel.Text = $"Статус: {status}";
                TimeLabel.Text = $"Время: {timestamp:yyyy-MM-dd HH:mm:ss}";
            });
        }

        // Обновление информации о последней активности пользователя
        public void UpdateUserActivity(string username, string action, DateTime timestamp)
        {
            _app.Invoke(() =>
            {
                ActivityLabel.Text = $"Последняя активность: {timestamp:HH:mm:ss} - {username} {action}";
            });
        }
    }

    sealed class Program
    {
        // Объект клиента, предполагается, что DemoClient реализует асинхронное соединение и события
        static DemoClient client = null!;
        static int subscriptionId;
        static TerminalGuiUI guiUI = null!;
        static ILogger logger = null!;
        static IApplication app = null!;

        static void Main(string[] args)
        {
            using IApplication application = Application.Create();
            app = application;
            app.Init();

            // Инициализируем пользовательский интерфейс
            guiUI = new TerminalGuiUI(app);
            guiUI.InitializeUI();

            // Опции меню
            string[] menuOptions = new string[]
            {
                "Сложить два числа",
                "Вычесть два числа",
                "Отписаться от событий",
                "Повторно подписаться на события",
                "Выход"
            };

            // Создаем ListView для выбора команды
            var menuListView = new ListView
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(2),
                Height = menuOptions.Length + 2
            };
            menuListView.SetSource(new ObservableCollection<string>(menuOptions));

            // Обработчик выбора пункта меню.
            // Для асинхронных вызовов используем async-лямбду.
            menuListView.Accepting += async (_, e) =>
            {
                e.Handled = true;
                if (menuListView.SelectedItem is not int index || index < 0 || index >= menuOptions.Length)
                    return;
                string selected = menuOptions[index];
                switch (selected)
                {
                    case "Сложить два числа":
                        {
                            int a = PromptForInt("Введите первое число:");
                            int b = PromptForInt("Введите второе число:");
                            int result = await client.Calculator.Add(a, b);
                            MessageBox.Query(app, 50, 7, "Результат", $"{a} + {b} = {result}", "OK");
                            break;
                        }
                    case "Вычесть два числа":
                        {
                            int a = PromptForInt("Введите первое число:");
                            int b = PromptForInt("Введите второе число:");
                            int result = await client.Calculator.Subtract(a, b);
                            MessageBox.Query(app, 50, 7, "Результат", $"{a} - {b} = {result}", "OK");
                            break;
                        }
                    case "Отписаться от событий":
                        {
                            bool result = await client.UnsubscribeAsync(subscriptionId);
                            MessageBox.Query(app, 50, 7, "Отписка", $"Отписка: {result}", "OK");
                            break;
                        }
                    case "Повторно подписаться на события":
                        {
                            subscriptionId = await client.SubscribeAsync(new[] { ServerEventType.SystemStatus, ServerEventType.UserActivity });
                            MessageBox.Query(app, 50, 7, "Подписка", $"Подписка оформлена с идентификатором: {subscriptionId}", "OK");
                            break;
                        }
                    case "Выход":
                        {
                            app.RequestStop();
                            break;
                        }
                }
            };

            // Добавляем ListView в рамку меню
            guiUI.MenuFrame.Add(menuListView);

            // Корневе вікно (v2: замість Application.Top) і створені рамки
            using var top = new Window { Title = "SimpleClient" };
            top.Add(guiUI.DashboardFrame, guiUI.MenuFrame);

            // Запускаем подключение к серверу в фоновом потоке
            Task.Run(async () =>
            {
                var loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.AddDebug()
                           .SetMinimumLevel(LogLevel.Information);
                });
                logger = loggerFactory.CreateLogger("TerminalGuiClient");

                // Соединение с сервером
                Uri serverUri = new Uri("ws://localhost:9000");
                client = new DemoClient(serverUri, logger);
                await client.ConnectAsync();

                // Подписываемся на события
                subscriptionId = await client.SubscribeAsync(new[] { ServerEventType.SystemStatus, ServerEventType.UserActivity });

                // Обновление UI при получении событий
                client.OnSystemStatus += (sender, e) =>
                {
                    guiUI.UpdateSystemStatus(e.Status, e.Timestamp);
                };
                client.OnUserActivity += (sender, e) =>
                {
                    guiUI.UpdateUserActivity(e.Username, e.Action, e.Timestamp);
                };
            });

            // Запуск основного цикла приложения
            app.Run(top);

            // По завершении работы отключаем клиента
            Task.Run(async () =>
            {
                if (client != null)
                    await client.DisconnectAsync();
            }).Wait();
        }

        // Вспомогательный метод для запроса целочисленного ввода с использованием диалога Terminal.Gui
        static int PromptForInt(string prompt)
        {
            int result = 0;
            using var dialog = new Dialog { Title = prompt, Width = 60, Height = 7 };

            var textField = new TextField
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(1)
            };
            dialog.Add(textField);

            var okButton = new Button { Text = "OK", IsDefault = true };
            okButton.Accepting += (_, e) =>
            {
                e.Handled = true;
                _ = int.TryParse(textField.Text, out result);
                app.RequestStop(dialog);
            };
            dialog.AddButton(okButton);

            app.Run(dialog);
            return result;
        }

    }
}
