# EasyPipes
Simple C# library for NamedPipe IPC

Written against .NET 4.6

## Usage

Server usage:
```csharp
using EasyPipes;

// IService defines the IPC interface
public interface IService
{
   int Sum(int one, int two);
}

// Calculator is the server class
class Calculator : IService
{
   public int Sum(int one, int two)
   {
      return one + two;
   }
}

// start up
Server server = new Server("pipename");
server.RegisterService<IService>(new Calculator());
server.Start();

// eventual shutdown
server.Stop();
```

Client usage:
```csharp
using EasyPipes;

// IService defines the IPC interface
public interface IService
{
   int Sum(int one, int two);
}

// setup client
Client client = new Client("pipename");
IService service = client.GetServiceProxy<IService>();

// execute remote operation
int result = service.Sum(6, 12); // = 18
```

Remarks:
* There is a single instance of the server class used by all clients!
* It's primarily focussed on IPC on single-machine.
* Any and all arguments or return values are serialized using DataContractSerializer, so custom types that need to cross the connection should be designed accordingly.

## Licence

Mozilla Public Licence 2.0

In simple terms: You can use the library as-is for any of your own projects, but any changes to the library itself have to be available under the same terms.
