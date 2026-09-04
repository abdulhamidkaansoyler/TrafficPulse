# TrafficPulse

A lightweight, system-tray based Windows network traffic monitoring tool built with C# and Windows Forms. TrafficPulse silently runs in the background, providing real-time network speeds and tracking your bandwidth usage over time.

## Features
* **Real-Time Speed Monitoring:** Hover over the system tray icon to view your current download (DL) and upload (UL) speeds in dynamically formatted Kbit/s or Mbit/s.
* **Historical Data Tracking:** Double-click the tray icon to open a detailed statistics window showing your Daily, Weekly, Monthly, and Yearly data consumption in Gigabytes (GB).
* **Smart Interface Filtering:** Automatically ignores loopback, tunnel, and virtual network adapters (such as VMware, VirtualBox, or WSL) to prevent double-counting and provide strictly accurate internet usage.
* **Low Resource Footprint:** Optimized with automatic memory minimization routines (using Win32 API and Garbage Collection) to ensure it consumes negligible RAM while running 24/7.
* **Customizable:** Includes a built-in Dark/Light theme toggle and an optional "Start with Windows" registry integration.

## How It Works (Technical Overview)
1. **Data Collection:** The application polls active, physical network interfaces every second using the `System.Net.NetworkInformation` namespace.
2. **Delta Calculation:** It calculates the exact difference in bytes received and sent since the last second, ignoring sudden data spikes caused by adapter reconnections.
3. **Data Persistence:** Traffic increments are safely logged and accumulated into a local JSON file located at `%AppData%\TrafficPulseData.json` to persist data across system reboots.
4. **Memory Management:** Every 30 seconds, the application triggers a memory optimization call to keep the background process strictly lightweight.

## Getting Started
1. Download or build the project using Visual Studio.
2. Run `TrafficPulse.exe`. 
3. The application will immediately initialize in your Windows system tray (bottom right corner).
4. **Hover** your mouse over the icon to see real-time speeds.
5. **Double-click** the icon to view your historical data usage statistics.
6. **Right-click** the icon to access the context menu for Dark Mode, Windows Startup settings, or to exit the application.