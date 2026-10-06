/// Задача с автоматической сложностью
public interface ICurriculum
{
    string Status { get; }
    float Bonus(int ok, int total);    // добавка к итогу, чтобы трудный уровень не проигрывал лёгкому
    void Update(int ok, int total);
}
