using System;
using System.Threading.Tasks;
using Terminal.Gui;
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

        public TerminalGuiUI()
        {
            // Инициализация будет выполнена в методе InitializeUI
        }

        public void InitializeUI()
        {
            // Создаем рамку для дашборда с фиксированной высотой
            DashboardFrame = new FrameView("Система мониторинга")
            {
                X = 0,
                Y = 0,
                Width = Dim.Percent(100),
                Height = 6
            };
            StatusLabel = new Label("Статус: N/A")
            {
                X = 1,
                Y = 1,
            };
            TimeLabel = new Label("Время: 0000-00-00 00:00:00")
            {
                X = 1,
                Y = 2,
            };
            ActivityLabel = new Label("Последняя активность: Нет активности")
            {
                X = 1,
                Y = 3,
            };
            DashboardFrame.Add(StatusLabel, TimeLabel, ActivityLabel);

            // Создаем рамку для меню, которая занимает оставшуюся часть экрана
            MenuFrame = new FrameView("Меню")
            {
                X = 0,
                Y = Pos.Bottom(DashboardFrame),
                Width = Dim.Percent(100),
                Height = Dim.Fill()
            };
        }

        // Обновление статуса системы (вызывается из обработчика событий)
        public void UpdateSystemStatus(string status, DateTime timestamp)
        {
            Application.MainLoop.Invoke(() =>
            {
                StatusLabel.Text = $"Статус: {status}";
                TimeLabel.Text = $"Время: {timestamp:yyyy-MM-dd HH:mm:ss}";
            });
        }

        // Обновление информации о последней активности пользователя
        public void UpdateUserActivity(string username, string action, DateTime timestamp)
        {
            Application.MainLoop.Invoke(() =>
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

        static void Main(string[] args)
        {
            Application.Init();

            // Инициализируем пользовательский интерфейс
            guiUI = new TerminalGuiUI();
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
            var menuListView = new ListView(menuOptions)
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(2),
                Height = menuOptions.Length + 2
            };

            // Обработчик выбора пункта меню.
            // Для асинхронных вызовов используем async-лямбду.
            menuListView.OpenSelectedItem += async (args) =>
            {
                string selected = args.Value.ToString()!;
                switch (selected)
                {
                    case "Сложить два числа":
                        {
                            int a = PromptForInt("Введите первое число:");
                            int b = PromptForInt("Введите второе число:");
                            int result = await client.Calculator.Add(a, b);
                            MessageBox.Query(50, 7, "Результат", $"{a} + {b} = {result}", "OK");
                            break;
                        }
                    case "Вычесть два числа":
                        {
                            int a = PromptForInt("Введите первое число:");
                            int b = PromptForInt("Введите второе число:");
                            int result = await client.Calculator.Subtract(a, b);
                            MessageBox.Query(50, 7, "Результат", $"{a} - {b} = {result}", "OK");
                            break;
                        }
                    case "Отписаться от событий":
                        {
                            bool result = await client.UnsubscribeAsync(subscriptionId);
                            MessageBox.Query(50, 7, "Отписка", $"Отписка: {result}", "OK");
                            break;
                        }
                    case "Повторно подписаться на события":
                        {
                            subscriptionId = await client.SubscribeAsync(new[] { ServerEventType.SystemStatus, ServerEventType.UserActivity });
                            MessageBox.Query(50, 7, "Подписка", $"Подписка оформлена с идентификатором: {subscriptionId}", "OK");
                            break;
                        }
                    case "Выход":
                        {
                            Application.RequestStop();
                            break;
                        }
                }
            };

            // Добавляем ListView в рамку меню
            guiUI.MenuFrame.Add(menuListView);

            // Получаем верхний контейнер и добавляем созданные рамки
            var top = Application.Top;
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
            Application.Run();

            // По завершении работы отключаем клиента
            Task.Run(async () =>
            {
                if (client != null)
                    await client.DisconnectAsync();
            }).Wait();

            Application.Shutdown();
        }

        // Вспомогательный метод для запроса целочисленного ввода с использованием диалога Terminal.Gui
        static int PromptForInt(string prompt)
        {
            int result = 0;
            var dialog = new Dialog(prompt, 60, 7);

            var textField = new TextField("")
            {
                X = 1,
                Y = 1,
                Width = Dim.Fill(1)
            };
            dialog.Add(textField);

            var okButton = new Button("OK");
            okButton.Clicked += () =>
            {
                _ = int.TryParse(textField.Text.ToString(), out result);
                Application.RequestStop();
            };
            dialog.AddButton(okButton);

            Application.Run(dialog);
            return result;
        }

    }
}
