#include <WiFi.h>
#include <Wire.h>
#include <Adafruit_MPU6050.h>
#include <Adafruit_Sensor.h>

// WiFi and Server Configuration
const char* ssid = "";
const char* password = "";
const char* host_ip = "192.168.1.9"; // IMPORTANT: Use your PC's IP address
const uint16_t port = 12345;

WiFiClient client;
Adafruit_MPU6050 mpu;

// Gyroscope calibration offsets
float gyroXoffset = 0;
float gyroYoffset = 0;
float gyroZoffset = 0;

// Variables for Complementary Filter
float angleX = 0;
float angleY = 0;
float angleZ = 0; // Yaw angle (will still drift without a magnetometer)

unsigned long last_update_time = 0;

void calibrateGyro() {
  Serial.println("Calibrating Gyroscope... Keep the sensor flat and still!");
  delay(1000);

  const int num_samples = 2000;
  for(int i = 0; i < num_samples; i++){
    sensors_event_t a, g, temp;
    mpu.getEvent(&a, &g, &temp);
    gyroXoffset += g.gyro.x;
    gyroYoffset += g.gyro.y;
    gyroZoffset += g.gyro.z;
    delay(1);
  }
  gyroXoffset /= num_samples;
  gyroYoffset /= num_samples;
  gyroZoffset /= num_samples;

  Serial.println("Calibration complete.");
  Serial.print("X offset: "); Serial.println(gyroXoffset);
  Serial.print("Y offset: "); Serial.println(gyroYoffset);
  Serial.print("Z offset: "); Serial.println(gyroZoffset);
  delay(1000);
}


void setup() {
  Serial.begin(115200);
  Wire.begin();
  
  // Connect to Wi-Fi
  WiFi.begin(ssid, password);
  Serial.print("Connecting to WiFi...");
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println(" Connected!");

  // Initialize MPU6050
  if (!mpu.begin()) {
    Serial.println("Failed to find MPU6050 chip");
    while (1) { delay(10); }
  }
  
  // Set sensor ranges
  mpu.setAccelerometerRange(MPU6050_RANGE_2_G);
  mpu.setGyroRange(MPU6050_RANGE_500_DEG);
  mpu.setFilterBandwidth(MPU6050_BAND_44_HZ);

  // Calibrate the gyroscope
  calibrateGyro();

  // Connect to PC TCP Server
  Serial.print("Connecting to server...");
  while (!client.connect(host_ip, port)) {
    Serial.println(" connection failed. Retrying...");
    delay(1000);
  }
  Serial.println(" Connected to server!");
  last_update_time = micros();
}

void loop() {
  if (!client.connected()) {
    // If connection is lost, try to reconnect
    while (!client.connect(host_ip, port)) {
      delay(1000);
    }
  }

  sensors_event_t a, g, temp;
  mpu.getEvent(&a, &g, &temp);

  // Calculate delta time
  unsigned long current_time = micros();
  float dt = (current_time - last_update_time) / 1000000.0;
  last_update_time = current_time;

  // Apply calibration offsets to raw gyro data
  float gyroX = g.gyro.x - gyroXoffset;
  float gyroY = g.gyro.y - gyroYoffset;
  float gyroZ = g.gyro.z - gyroZoffset;

  // Calculate pitch (X-axis rotation) and roll (Y-axis rotation) from the accelerometer
  // These angles are in radians.
  float accelAngleX = atan2(a.acceleration.y, a.acceleration.z);
  float accelAngleY = atan2(-a.acceleration.x, sqrt(a.acceleration.y * a.acceleration.y + a.acceleration.z * a.acceleration.z));

  // Complementary filter
  // The weight 'alpha' determines how much to trust the gyro vs. the accelerometer
  // A common value is 0.98
  float alpha = 0.98;
  
  // Combine gyro and accelerometer data to get stable angles (in radians)
  angleX = alpha * (angleX + gyroX * dt) + (1 - alpha) * accelAngleX;
  angleY = alpha * (angleY + gyroY * dt) + (1 - alpha) * accelAngleY;
  angleZ += gyroZ * dt; // Yaw still drifts as accelerometer cannot correct it

  // Convert radians to degrees for easier interpretation/debugging if needed
  float angleX_deg = angleX * 180.0 / M_PI;
  float angleY_deg = angleY * 180.0 / M_PI;
  float angleZ_deg = angleZ * 180.0 / M_PI;
  
  // Send the stable calculated angles as CSV: angleX_deg,angleY_deg,angleZ_deg
  String data = String(angleX_deg) + "," + String(angleY_deg) + "," + String(angleZ_deg) + "\n";
  client.print(data);
}