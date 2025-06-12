using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IMU3D_MPU6050_WPF
{
    public partial class MainWindow : Window
    {
        // TCP Server Fields
        private TcpListener _tcpListener;
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private bool _isListening = false;
        private const int Port = 12345;

        // Game Logic Fields
        private DispatcherTimer _gameTimer;
        private bool _isGameRunning = false;

        // Player
        private double _playerVerticalVelocity = 0;
        private const double Gravity = 0.5;
        private bool _isJumping = false;

        // Obstacles
        private List<Rectangle> _obstacles = new List<Rectangle>();
        private const double ObstacleSpeed = 5;
        private int _obstacleSpawnCounter = 0;
        private Random _random = new Random();

        // Score
        private int _score = 0;

        // --- NEW: IMU Dynamic Jump Detection ---
        private float _previousAngleX = 0;

        public MainWindow()
        {
            InitializeComponent();
            _gameTimer = new DispatcherTimer();
            _gameTimer.Tick += GameLoop;
            _gameTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 FPS
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Task.Run(() => StartServer());
            StartGame();
        }

        private void StartGame()
        {
            // Reset game state
            _score = 0;
            ScoreText.Text = "0";
            _playerVerticalVelocity = 0;
            _isJumping = false;
            _previousAngleX = 0; // Reset previous angle on game start
            Canvas.SetBottom(Player, 10); // Start on the ground

            // Clear old obstacles
            foreach (var obstacle in _obstacles)
            {
                GameCanvas.Children.Remove(obstacle);
            }
            _obstacles.Clear();
            _obstacleSpawnCounter = 0;

            // Hide game over text
            GameOverText.Visibility = Visibility.Collapsed;
            RestartText.Visibility = Visibility.Collapsed;

            // Start game
            _isGameRunning = true;
            _gameTimer.Start();
        }

        private void GameLoop(object sender, EventArgs e)
        {
            if (!_isGameRunning) return;

            // Player physics
            _playerVerticalVelocity -= Gravity;
            double playerBottom = Canvas.GetBottom(Player);
            playerBottom += _playerVerticalVelocity;

            if (playerBottom <= 10) // On the ground
            {
                playerBottom = 10;
                _playerVerticalVelocity = 0;
                _isJumping = false;
            }
            Canvas.SetBottom(Player, playerBottom);

            // Obstacle handling
            _obstacleSpawnCounter++;
            if (_obstacleSpawnCounter > 100)
            {
                SpawnObstacle();
                _obstacleSpawnCounter = 0;
            }

            Rect playerHitbox = new Rect(Canvas.GetLeft(Player), Canvas.GetBottom(Player), Player.Width, Player.Height);

            foreach (var obstacle in _obstacles.ToList())
            {
                double obstacleLeft = Canvas.GetLeft(obstacle);
                obstacleLeft -= ObstacleSpeed;
                Canvas.SetLeft(obstacle, obstacleLeft);

                if (obstacleLeft < -obstacle.Width)
                {
                    GameCanvas.Children.Remove(obstacle);
                    _obstacles.Remove(obstacle);
                    _score++;
                    ScoreText.Text = _score.ToString();
                }

                Rect obstacleHitbox = new Rect(obstacleLeft, Canvas.GetBottom(obstacle), obstacle.Width, obstacle.Height);
                if (playerHitbox.IntersectsWith(obstacleHitbox))
                {
                    EndGame();
                }
            }
        }

        private void SpawnObstacle()
        {
            double height = _random.Next(30, 70);
            Rectangle obstacle = new Rectangle
            {
                Fill = Brushes.DarkRed,
                Width = 40,
                Height = height
            };
            Canvas.SetLeft(obstacle, GameCanvas.ActualWidth);
            Canvas.SetBottom(obstacle, 10); // on the ground
            _obstacles.Add(obstacle);
            GameCanvas.Children.Add(obstacle);
        }

        private void Jump(double force)
        {
            if (!_isJumping)
            {
                _isJumping = true;
                _playerVerticalVelocity = force;
            }
        }

        private void EndGame()
        {
            _isGameRunning = false;
            _gameTimer.Stop();
            GameOverText.Visibility = Visibility.Visible;
            RestartText.Visibility = Visibility.Visible;

            Canvas.SetLeft(GameOverText, (GameCanvas.ActualWidth - GameOverText.ActualWidth) / 2);
            Canvas.SetTop(GameOverText, (GameCanvas.ActualHeight / 2) - 50);
            Canvas.SetLeft(RestartText, (GameCanvas.ActualWidth - RestartText.ActualWidth) / 2);
            Canvas.SetTop(RestartText, (GameCanvas.ActualHeight / 2));
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.R && !_isGameRunning)
            {
                StartGame();
            }
        }

        #region TCP Server and IMU Data Handling

        private async void StartServer()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, Port);
                _tcpListener.Start();
                _isListening = true;

                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    var host = Dns.GetHostEntry(Dns.GetHostName());
                    string ipAddress = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "?.?.?.?";
                    StatusText.Text = $"Listening on {ipAddress}:{Port}...";
                    StatusText.Foreground = Brushes.Yellow;
                }));

                while (_isListening)
                {
                    _tcpClient = await _tcpListener.AcceptTcpClientAsync();
                    _stream = _tcpClient.GetStream();

                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatusText.Text = "Client Connected!";
                        StatusText.Foreground = Brushes.LawnGreen;
                    }));

                    await ListenForData();

                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatusText.Text = "Client Disconnected. Awaiting new connection...";
                        StatusText.Foreground = Brushes.OrangeRed;
                    }));
                }
            }
            catch (Exception ex)
            {
                if (_isListening)
                {
                    MessageBox.Show($"Server Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task ListenForData()
        {
            var reader = new StreamReader(_stream);
            while (_isListening && _tcpClient.Connected)
            {
                try
                {
                    var dataString = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(dataString)) continue;

                    var parts = dataString.Split(',');
                    if (parts.Length < 3) continue;

                    float angleX = float.Parse(parts[0], CultureInfo.InvariantCulture);

                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ProcessImuData(angleX);
                    }));
                }
                catch (IOException) { break; }
                catch (Exception) { /* Handle parsing errors if necessary */ }
            }
        }

        private void ProcessImuData(float angleX)
        {
            float deltaAngle = angleX - _previousAngleX;
            _previousAngleX = angleX;

            if (!_isJumping && deltaAngle > 2.0f && angleX > 5.0f)
            {
                // Feel free to tune these values to get the jump feel you want!
                const double minJumpForce = 8.0;  // Force for the smallest jump
                const double maxJumpForce = 18.0; // Force for the highest jump
                const double forceScale = 1.5;    // How much the tilt speed affects the force

                double calculatedJumpForce = minJumpForce + (deltaAngle * forceScale);

                // Clamp the force to our defined min/max range to keep it predictable
                double finalJumpForce = Math.Clamp(calculatedJumpForce, minJumpForce, maxJumpForce);

                // Execute the jump with the calculated force
                Jump(finalJumpForce);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _isListening = false;
            _tcpClient?.Close();
            _tcpListener?.Stop();
        }

        #endregion
    }
}