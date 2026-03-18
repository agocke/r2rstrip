// More substantial test application with classes and methods
using System;

Console.WriteLine("Hello from R2R test app!");

var calculator = new Calculator();
var result = calculator.Add(10, 20);
Console.WriteLine($"10 + 20 = {result}");

result = calculator.Multiply(5, 6);
Console.WriteLine($"5 * 6 = {result}");

var person = new Person { Name = "Alice", Age = 30 };
Console.WriteLine($"Person: {person.Name}, Age: {person.Age}");

// Exercise generics
var repo = new Repository<Person>();
repo.Add(person);
repo.Add(new Person { Name = "Bob", Age = 25 });
Console.WriteLine($"Repository count: {repo.Count}");
Console.WriteLine($"First: {repo.Get(0)}");

public class Calculator
{
    public int Add(int a, int b) => a + b;
    
    public int Subtract(int a, int b) => a - b;
    
    public int Multiply(int a, int b) => a * b;
    
    public int Divide(int a, int b)
    {
        if (b == 0)
            throw new DivideByZeroException("Cannot divide by zero");
        return a / b;
    }
}

public class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    
    public override string ToString() => $"{Name} ({Age})";
}

public class Repository<T> where T : class
{
    private readonly List<T> _items = new();

    public void Add(T item) => _items.Add(item);
    public T Get(int index) => _items[index];
    public int Count => _items.Count;
}

