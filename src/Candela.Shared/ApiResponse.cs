namespace Candela.Shared
{
    /// <summary>
    /// The success envelope the tablet app already expects. Serialised with camelCase
    /// property names and null values omitted, so a successful call is
    ///
    ///     { "success": true, "data": { ... } }
    ///
    /// with "error" absent rather than null.
    ///
    /// This type lives in Contracts, targeting netstandard2.0, so the .NET Framework
    /// host and the .NET 10 API reference the same definition and cannot drift apart
    /// while both are serving traffic.
    ///
    /// Do not add, rename or reorder members without shipping a matching frontend
    /// release: 47 call sites in the React app read response.data.error, and the login
    /// flow reads response.data.data field by field.
    /// </summary>
    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public T? Data { get; set; }

        public static ApiResponse<T> Ok(T data) =>
            new ApiResponse<T> { Success = true, Data = data };

        public static ApiResponse<T> Fail(string error) =>
            new ApiResponse<T> { Success = false, Error = error };
    }
}
