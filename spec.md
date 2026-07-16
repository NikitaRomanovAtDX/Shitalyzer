# Roslyn Analyzers for our specific flow

### Summary
I want a diagnostic code SHIT**** (for example SHIT0001, 0002 etc) (Source Has Incompatible Traits)
Some diagnostics have a code fix (described below)

### Warning for `package` variable names.
Out code converts to Java. If you use `package` variable name, our code can't converts to java.
for example:
```cs
foreach(var package in ProdcutPackages) {
    // var package rejected in out converter
}
```

CodeFix: not required, but you can suggest to rename package to `_package` or something similar.

### Warning for using `String.StartsWith(char ch)`
Out code uses both .NET Core and .NET Framework (4.7.2). We are working with .NET Core project
and use some methods, that couldn't be used in the .NET Framework. Get me a warning for this methods.
For example:
```cs
string data = "abcde";
if (data.StartsWith('a'))
    // This overload doesn't exists in the netfx.
```

You can add not only startsWith analyzer, add another method that you can memorize.
CodeFix: you can suggest to rewrite this construction to avoid usage this methods, for example:
```cs
if (data.Length != 0 && data[0] == 'a')
//OK

if (data.StartsWith("a"))
//FAIL: roslyn analyzer suggests to use char overload
```

### Usage valueTypes in the lambda context
Our code converts to Java. If you define int and use it inside lambda - it can't be compiled.
For example:
```cs
int data = 0;
DoProcess(() => {
    data = 100;
})
// Error!
```

CodeFix: suggest to use class-holder for this variables:
```cs
int data = 0;
bool isProcessed = false;
DoProcess(() => {
    data = 100;
    isProcessed = false;
})

//Apply CodeFix:
class NewDataHolder {
    public int Data { get; set; } = 0;
    public bool IsProcessed { get; set; } = false;
}

var dataHolder = new DataHolder();
DoProcess(() => {
    dataHolder.Data = 100;
    dataHolder.IsProcessed = false;
})
```