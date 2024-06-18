# SimpleL7Proxy

The SimpleL7Proxy is a lightweight, efficient Layer 7 proxy designed to route network traffic between clients and servers. It operates at the application layer (Layer 7) of the OSI model, enabling it to inspect, route, and modify the content of network packets.

This proxy is capable of handling HTTP/HTTPS requests and responses, making it ideal for web applications. It can be used for load balancing, SSL termination, and other network-related tasks.

The project is written in C#, making use of the .NET Core framework for cross-platform compatibility. It is designed with simplicity and performance in mind, providing a straightforward way to manage network traffic at the application layer.

Features
HTTP/HTTPS traffic routing
Load balancing capabilities
SSL termination
Cross-platform compatibility (Windows, Linux, macOS)
Simple, easy-to-understand codebase
Usage
To use SimpleL7Proxy, you'll need to have .NET Core installed on your machine. Once installed, you can clone this repository, build the project, and run the resulting binary to start the proxy.

Please refer to the documentation for more detailed instructions and usage examples.

Environment Variables:

These environment variables are used to configure various aspects of the application. Here's a summary of how each one is used:

Host1=, Host2=, Host3=, etc.: These variables are used to specify the hostnames of the backend servers. Up to 9 backend hosts can be specified. If a hostname is provided, the application creates a new BackendHost instance and adds it to the hosts list.

Probe_path1, Probe_path2, Probe_path3, etc.: These variables are used to specify the probe paths for the corresponding backend hosts. If a Host variable is set, the application will attempt to read the corresponding Probe_path variable when creating the BackendHost instance.  If it's not set, the application defaults to use echo/resource?param1=sample.

Port: This variable is used to specify the port number that the server will listen on. If it's not set, the application defaults to port 443.

PollInterval: This variable is used to specify the interval (in milliseconds) at which the application will poll the backend servers. If it's not set, the application defaults to 15000 milliseconds (15 seconds).

APPINSIGHTS_CONNECTIONSTRING: This variable is used to specify the connection string for Azure Application Insights. If it's set, the application initializes Awill send logs to the application insights instance.

Example:

Port=8000
Host1=https://localhost:3000
Host2=http://localhost:5000
PollInterval=1500

This will create a listener on port 8000 and will check the health of the two hosts (https://localhost:3000 and http://localhost:5000) every 1.5 seconds.  Any incoming requests will be proxied to the server with the lowest latency ( as measured every PollInterval).  This will not log to Application Insights.



