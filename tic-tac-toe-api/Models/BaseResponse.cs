﻿using Microsoft.AspNetCore.Http;
using Silence.Infrastructure.Helpers;

namespace tic_tac_toe_api.Models;

public class BaseResponse<T> : IBaseResponse<T>
{
    public string Description { get; set; }

    public StatusCode StatusCode { get; set; }

    public T Data { get; set; }
}

public interface IBaseResponse<T>
{
    string Description { get; }
    StatusCode StatusCode { get; }
    T Data { get; }
}