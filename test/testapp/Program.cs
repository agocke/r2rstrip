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

