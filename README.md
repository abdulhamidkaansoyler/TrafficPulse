# TrafficPulse

A lightweight, system-tray based Windows network traffic monitoring tool built with C# and Windows Forms. TrafficPulse silently runs in the background, providing real-time network speeds and tracking your bandwidth usage over time.

## Features
* **Real-Time Speed Monitoring:** Hover over the system tray icon to view your current download (DL) and upload (UL) speeds in dynamically formatted KB/s or MB/s.
* **Historical Data Tracking:** Double-click the tray icon to open a detailed statistics window showing your Daily, Weekly, Monthly, and Yearly data consumption in Gigabytes (GB).
* **Smart Interface Filtering:** Ignores loopback, tunnel, VPN, and common virtual network adapters (such as VMware, VirtualBox, Hyper-V, or WSL) to reduce double-counting.
* **Low Resource Footprint:** Uses a background monitoring thread and compact daily records for continuous operation.
* **Customizable:** Includes a built-in Dark/Light theme toggle and an optional "Start with Windows" registry integration.

## How It Works (Technical Overview)
1. **Data Collection:** The application polls eligible active network interfaces every second using the `System.Net.NetworkInformation` namespace.
2. **Delta Calculation:** It tracks each network interface independently and calculates the difference in bytes received and sent since the previous sample. Newly connected interfaces are baselined before their traffic is counted.
3. **Data Persistence:** Traffic increments are safely logged and accumulated into a local JSON file located at `%AppData%\TrafficPulse\TrafficPulseData.json` to persist data across system reboots.

## Getting Started
1. Download or build the project using Visual Studio.
2. Run `TrafficPulse.exe`. 
3. The application will immediately initialize in your Windows system tray (bottom right corner).
4. **Hover** your mouse over the icon to see real-time speeds.
5. **Double-click** the icon to view your historical data usage statistics.
6. **Right-click** the icon to access the context menu for Dark Mode, Windows Startup settings, or to exit the application.
